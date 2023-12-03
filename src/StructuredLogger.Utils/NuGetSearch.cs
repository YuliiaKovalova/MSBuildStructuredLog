﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NuGet.ProjectModel;
using StructuredLogViewer;

namespace Microsoft.Build.Logging.StructuredLogger
{
    public class AssetsFile
    {
        public string AssetsFilePath { get; set; }
        public string ProjectFilePath { get; set; }
        public string Text { get; set; }

        private LockFile lockFile;
        public LockFile LockFile
        {
            get
            {
                lock (this)
                {
                    lockFile ??= new LockFileFormat().Parse(Text, AssetsFilePath);
                }

                return lockFile;
            }
        }
    }

    public class NuGetSearch : ISearchExtension
    {
        private Regex projectFilePathRegex = new Regex(@"\""projectPath\""\: \""(?<Path>[^\""]+)\"",", RegexOptions.Compiled);
        private List<AssetsFile> assetsFiles;

        public Build Build { get; }

        public NuGetSearch(Build build)
        {
            Build = build;
        }

        public bool TryGetResults(NodeQueryMatcher matcher, IList<SearchResult> resultCollector, int maxResults)
        {
            if (!string.Equals(matcher.TypeKeyword, "nuget", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var underProjectMatcher = matcher.IncludeMatchers.FirstOrDefault(m => m.UnderProject);
            if (underProjectMatcher == null || underProjectMatcher.Terms.Count == 0)
            {
                resultCollector.Add(new SearchResult(new Error { Text = "Add a 'project(...)' clause to filter which project(s) to search." }));
                resultCollector.Add(new SearchResult(new Note { Text = "Specify '$nuget project(.csproj)' to search all projects (expensive)." }));
                return true;
            }

            PopulateAssetsFiles();

            var files = FindAssetsFiles(underProjectMatcher, maxResults);
            if (files.Count == 0)
            {
                resultCollector.Add(new SearchResult(new Error { Text = "No matching project.assets.json files found" }));
                return true;
            }

            foreach (var file in files)
            {
                var project = new Project
                {
                    ProjectFile = file.ProjectFilePath,
                    Name = Path.GetFileName(file.ProjectFilePath),
                    IsExpanded = true
                };

                PopulateProject(project, matcher, file);

                if (project.HasChildren)
                {
                    resultCollector.Add(new SearchResult(project));
                }
            }

            return true;
        }

        private void PopulateProject(Project project, NodeQueryMatcher matcher, AssetsFile file)
        {
            var lockFile = file.LockFile;

            bool expand = matcher.Terms.Count > 0;
            bool addedAnything = false;

            Dictionary<string, Folder> targetsByDependencyHash = new(StringComparer.OrdinalIgnoreCase);

            foreach (var framework in lockFile.ProjectFileDependencyGroups)
            {
                string frameworkName = framework.FrameworkName;
                var frameworkDependencies = framework.Dependencies.Select(d => ParsePackageId(d)).ToArray();

                var target = lockFile.Targets.FirstOrDefault(t => t.Name == frameworkName);
                if (target == null)
                {
                    continue;
                }

                var libraries = target.Libraries.ToDictionary(l => l.Name);

                var frameworkNode = new Folder
                {
                    Name = $"Dependencies for {frameworkName}",
                    IsExpanded = true
                };

                HashSet<string> expandedPackages = new(frameworkDependencies.Select(d => d.name));

                var topLevelLibraries = new List<LockFileTargetLibrary>();

                foreach (var name in expandedPackages)
                {
                    if (!libraries.TryGetValue(name, out var topLibrary))
                    {
                        continue;
                    }

                    topLevelLibraries.Add(topLibrary);
                }

                var topLevelLibrariesSorted = topLevelLibraries.OrderByDescending(l => l.Type);

                string dependenciesHash = string.Join(",", topLevelLibrariesSorted.Select(t => $"{t.Name}/{t.Version}"));
                if (targetsByDependencyHash.TryGetValue(dependenciesHash, out var existingFolder))
                {
                    existingFolder.Name = $"{existingFolder.Name}; {frameworkName}";
                    continue;
                }

                targetsByDependencyHash[dependenciesHash] = frameworkNode;

                foreach (var topLibrary in topLevelLibrariesSorted)
                {
                    var dependency = frameworkDependencies.FirstOrDefault(d => d.name == topLibrary.Name).version;
                    (TreeNode topLevelNode, SearchResult match) = CreateNode(lockFile, dependency, topLibrary, expand, matcher);

                    bool added = AddDependencies(lockFile, topLibrary.Name, topLevelNode, libraries, expandedPackages, matcher);
                    if (match != null || added)
                    {
                        frameworkNode.AddChild(topLevelNode);
                        addedAnything = true;
                    }
                }

                if (addedAnything)
                {
                    project.AddChild(frameworkNode);
                }
            }

            if (!addedAnything)
            {
                return;
            }

            PopulatePackageFolders(project, lockFile);
            PopulateLogs(project, lockFile);
            PopulatePackageContents(project, lockFile, matcher);
        }

        private bool AddDependencies(
            LockFile lockFile,
            string id,
            TreeNode dependencyNode,
            Dictionary<string, LockFileTargetLibrary> libraries,
            HashSet<string> expandedPackages,
            NodeQueryMatcher matcher)
        {
            if (!libraries.TryGetValue(id, out var library))
            {
                return false;
            }

            bool result = false;
            bool expand = matcher.Terms.Count > 0;

            var dependencyLibraries = new List<LockFileTargetLibrary>();

            foreach (var name in library.Dependencies.Select(d => d.Id))
            {
                if (!libraries.TryGetValue(name, out var dependencyLibrary))
                {
                    continue;
                }

                dependencyLibraries.Add(dependencyLibrary);
            }

            var dependencyLibrariesSorted = dependencyLibraries.OrderByDescending(l => l.Type);

            HashSet<LockFileTargetLibrary> needToAddChildren = new();

            foreach (var dependencyLibrary in dependencyLibrariesSorted)
            {
                if (expandedPackages.Add(dependencyLibrary.Name))
                {
                    needToAddChildren.Add(dependencyLibrary);
                }
            }

            foreach (var dependencyLibrary in dependencyLibrariesSorted)
            {
                var dependency = library.Dependencies.FirstOrDefault(d => d.Id == dependencyLibrary.Name).VersionRange.ToString();
                var (node, match) = CreateNode(lockFile, dependency, dependencyLibrary, expand, matcher);

                bool added = false;
                if (needToAddChildren.Contains(dependencyLibrary))
                {
                    added = AddDependencies(lockFile, dependencyLibrary.Name, node, libraries, expandedPackages, matcher);
                }

                if (match != null || added)
                {
                    dependencyNode.AddChild(node);
                    result = true;
                }
            }

            return result;
        }

        private void PopulatePackageContents(Project project, LockFile lockFile, NodeQueryMatcher matcher)
        {
            var nodesByTarget = new Dictionary<string, (List<string> targets, TreeNode node)>();

            foreach (var target in lockFile.Targets)
            {
                foreach (var package in target.Libraries)
                {
                    if (package.Type == "project")
                    {
                        continue;
                    }

                    var match = matcher.IsMatch(package.Name, package.Version.ToString());
                    if (match == null || match == SearchResult.EmptyQueryMatch)
                    {
                        continue;
                    }

                    var node = AddPackage(package);
                    string contentHash = StringWriter.GetString(node);
                    if (nodesByTarget.TryGetValue(contentHash, out var existing))
                    {
                        existing.targets.Add(target.Name);
                    }
                    else
                    {
                        nodesByTarget[contentHash] = (new List<string> { target.Name }, node);
                    }
                }
            }

            if (nodesByTarget.Count == 1)
            {
                project.AddChild(nodesByTarget.FirstOrDefault().Value.node);
            }
            else
            {
                foreach (var kvp in nodesByTarget)
                {
                    var folderName = string.Join(",", kvp.Value.targets);
                    var folder = project.GetOrCreateNodeWithName<Folder>(folderName);
                    var node = kvp.Value.node;
                    node.IsExpanded = false;
                    folder.IsExpanded = true;
                    folder.AddChild(node);
                }
            }
        }

        private TreeNode AddPackage(LockFileTargetLibrary package)
        {
            var node = new Package
            {
                Name = package.Name,
                Version = package.Version.ToString(),
                IsExpanded = true
            };

            if (package.Dependencies.Count > 0)
            {
                var folder = new Folder { Name = "Dependencies", IsExpanded = true };
                node.AddChild(folder);

                foreach (var item in package.Dependencies)
                {
                    var itemNode = new Package { Name = item.Id, VersionSpec = item.VersionRange.ToString() };
                    folder.AddChild(itemNode);
                }
            }

            AddItems(node, package.Build, "build");
            AddItems(node, package.BuildMultiTargeting, "buildMultitargeting");
            AddItems(node, package.FrameworkAssemblies, "frameworkAssemblies");
            AddItems(node, package.CompileTimeAssemblies, "compile");
            AddItems(node, package.ContentFiles.OfType<LockFileItem>().ToArray(), "contentFiles");
            AddItems(node, package.EmbedAssemblies, "embed");
            AddItems(node, package.NativeLibraries, "native");
            AddItems(node, package.ResourceAssemblies, "resource");
            AddItems(node, package.RuntimeAssemblies, "runtime");
            AddItems(node, package.RuntimeTargets.OfType<LockFileItem>().ToArray(), "runtimeTargets");
            AddItems(node, package.ToolsAssemblies, "tools");
            AddItems(node, package.FrameworkReferences, "frameworkReferences");

            return node;
        }

        private void AddItems(Package node, IList<string> items, string itemName)
        {
            if (items == null || items.Count == 0)
            {
                return;
            }

            var folder = new Folder { Name = itemName, IsExpanded = true };
            node.AddChild(folder);

            foreach (var item in items)
            {
                var itemNode = new Item { Name = item };
                folder.AddChild(itemNode);
            }
        }

        private void AddItems(Package node, IList<LockFileItem> items, string itemName)
        {
            if (items == null || items.Count == 0)
            {
                return;
            }

            var folder = new Folder { Name = itemName, IsExpanded = true };
            node.AddChild(folder);

            foreach (var item in items)
            {
                var itemNode = new Item { Name = item.Path };
                if (item.Properties is { } properties && properties.Count > 0)
                {
                    foreach (var property in properties)
                    {
                        var metadata = new Metadata { Name = property.Key, Value = property.Value };
                        itemNode.AddChild(metadata);
                    }
                }

                folder.AddChild(itemNode);
            }
        }

        private void PopulateLogs(Project project, LockFile lockFile)
        {
            foreach (var logMessage in lockFile.LogMessages)
            {
                string text = logMessage.Message;

                TextNode node;
                if (logMessage.Level == NuGet.Common.LogLevel.Error)
                {
                    node = new Error
                    {
                        Code = logMessage.Code.ToString()
                    };
                }
                else if (logMessage.Level == NuGet.Common.LogLevel.Warning)
                {
                    node = new Warning
                    {
                        Code = logMessage.Code.ToString()
                    };
                }
                else
                {
                    node = new MessageWithLocation();
                    text = $"{logMessage.Code}: {text}";
                }

                node.Text = text;

                project.AddChild(node);
            }
        }

        private static void PopulatePackageFolders(Project project, LockFile lockFile)
        {
            var packageFoldersNode = new Folder
            {
                Name = "PackageFolders",
                IsLowRelevance = true
            };
            foreach (var packageFolder in lockFile.PackageFolders)
            {
                var item = new Item { Name = packageFolder.Path };
                packageFoldersNode.AddChild(item);
            }

            if (packageFoldersNode.HasChildren)
            {
                project.AddChild(packageFoldersNode);
            }
        }

        private (TreeNode node, SearchResult match) CreateNode(
            LockFile lockFile,
            string dependency,
            LockFileTargetLibrary library,
            bool expand,
            NodeQueryMatcher matcher)
        {
            string name = library.Name;
            string version = library.Version.ToString();

            TreeNode node;
            SearchResult match;

            if (library.Type == "project")
            {
                var libraryInfo = lockFile.GetLibrary(name, library.Version);
                name = Path.GetFileName(libraryInfo.MSBuildProject);
                node = new Project
                {
                    Name = name,
                    ProjectFile = libraryInfo.MSBuildProject,
                    IsExpanded = expand
                };
                match = matcher.IsMatch(name);
            }
            else
            {
                node = new Package
                {
                    Name = name,
                    Version = version,
                    VersionSpec = dependency,
                    IsExpanded = expand
                };
                match = matcher.IsMatch(name, version, dependency);
            }

            if (match != null && match != SearchResult.EmptyQueryMatch)
            {
                if (node is Package package)
                {
                    match.FieldsToDisplay = new List<string>
                    {
                        package.Name,
                        package.Version,
                        package.VersionSpec
                    };
                }

                var proxy = new ProxyNode();
                proxy.Original = node;
                proxy.Populate(match);
                proxy.Text = node.ToString();
                proxy.IsExpanded = expand;

                var children = node.Children.ToArray();
                node.Children.Clear();
                foreach (var child in children)
                {
                    proxy.AddChild(child);
                }

                node = proxy;
            }

            return (node, match);
        }

        private (string name, string version) ParsePackageId(string dependency)
        {
            return dependency.GetFirstAndRest(' ');
        }

        private IReadOnlyList<AssetsFile> FindAssetsFiles(NodeQueryMatcher underProjectMatcher, int maxResults)
        {
            var files = new List<AssetsFile>();
            foreach (var assetFile in assetsFiles)
            {
                foreach (var term in underProjectMatcher.Terms)
                {
                    if (term.IsMatch(assetFile.ProjectFilePath) ||
                        term.IsMatch(assetFile.AssetsFilePath))
                    {
                        files.Add(assetFile);
                        if (files.Count >= maxResults)
                        {
                            return files;
                        }
                    }
                }
            }

            return files;
        }

        private void PopulateAssetsFiles()
        {
            if (assetsFiles != null)
            {
                return;
            }

            assetsFiles = new List<AssetsFile>();

            if (Build.SourceFiles == null || Build.SourceFiles.Count == 0)
            {
                return;
            }

            foreach (var file in Build.SourceFiles)
            {
                if (!file.FullPath.EndsWith("project.assets.json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var assetFile = new AssetsFile
                {
                    AssetsFilePath = file.FullPath,
                    Text = file.Text
                };

                var match = projectFilePathRegex.Match(file.Text);
                if (!match.Success)
                {
                    continue;
                }

                string projectFilePath = match.Groups["Path"].Value;
                if (string.IsNullOrEmpty(projectFilePath))
                {
                    continue;
                }

                projectFilePath = projectFilePath.Replace(@"\\", @"\");

                assetFile.ProjectFilePath = projectFilePath;

                assetsFiles.Add(assetFile);
            }
        }
    }
}