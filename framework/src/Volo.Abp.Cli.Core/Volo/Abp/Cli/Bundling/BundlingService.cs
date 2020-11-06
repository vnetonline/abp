﻿using Microsoft.CodeAnalysis.CSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using Volo.Abp.Bundling;
using Volo.Abp.Cli.Build;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Modularity;

namespace Volo.Abp.Cli.Bundling
{
    public class BundlingService : IBundlingService, ITransientDependency
    {
        const string StylePlaceholderStart = "<!--ABP:Styles-->";
        const string StylePlaceholderEnd = "<!--/ABP:Styles-->";
        const string ScriptPlaceholderStart = "<!--ABP:Scripts-->";
        const string ScriptPlaceholderEnd = "<!--/ABP:Scripts-->";
        const string SupportedWebAssemblyProjectType = "Microsoft.NET.Sdk.BlazorWebAssembly";

        public IDotNetProjectBuilder DotNetProjectBuilder { get; set; }

        public async Task BundleAsync(string directory, bool forceBuild)
        {
            var projectFiles = Directory.GetFiles(directory, "*.csproj");
            if (!projectFiles.Any())
            {
                throw new BundlingException("No project file found in the directory");
            }

            var projectFilePath = projectFiles[0];

            if (forceBuild)
            {
                var projects = new List<DotNetProjectInfo>()
                {
                    new DotNetProjectInfo(string.Empty, projectFilePath, true)
                };
                DotNetProjectBuilder.Build(projects, string.Empty);
            }

            var frameworkVersion = GetTargetFrameworkVersion(projectFilePath);
            var assemblyFilePath = GetAssemblyFilePath(directory, frameworkVersion, Path.GetFileNameWithoutExtension(projectFilePath));
            var startupModule = GetStartupModule(assemblyFilePath);

            var bundleDefinitions = new List<BundleTypeDefinition>();
            FindBundleContributersRecursively(startupModule, 0, bundleDefinitions);
            bundleDefinitions = bundleDefinitions.OrderByDescending(t => t.Level).ToList();

            var styleContext = GetStyleContext(bundleDefinitions);
            var scriptContext = GetScriptContext(bundleDefinitions);
            var styleDefinitons = GenerateStyleDefinitions(styleContext);
            var scriptDefinitions = GenerateScriptDefinitions(scriptContext);

            await UpdateDependenciesInHtmlFileAsync(directory, styleDefinitons, scriptDefinitions);
        }

        private BundleContext GetScriptContext(List<BundleTypeDefinition> bundleDefinitions)
        {
            var scriptContext = new BundleContext();
            foreach (var bundleDefinition in bundleDefinitions)
            {
                var contributer = CreateContributerInstance(bundleDefinition.BundleContributerType);
                contributer.AddScripts(scriptContext);
            }
            scriptContext.Add("_framework/blazor.webassembly.js");
            return scriptContext;
        }

        private BundleContext GetStyleContext(List<BundleTypeDefinition> bundleDefinitions)
        {
            var styleContext = new BundleContext();
            foreach (var bundleDefinition in bundleDefinitions)
            {
                var contributer = CreateContributerInstance(bundleDefinition.BundleContributerType);
                contributer.AddStyles(styleContext);
            };
            return styleContext;
        }

        private async Task UpdateDependenciesInHtmlFileAsync(string directory, string styleDefinitions, string scriptDefinitions)
        {
            var htmlFilePath = Path.Combine(directory, "wwwroot", "index.html");
            if (!File.Exists(htmlFilePath))
            {
                throw new BundlingException($"index.html file could not be found in the following path:{htmlFilePath}");
            }

            Encoding fileEncoding;
            string content;
            using (var reader = new StreamReader(htmlFilePath, true))
            {
                fileEncoding = reader.CurrentEncoding;
                content = await reader.ReadToEndAsync();
            }

            content = UpdatePlaceholders(content, StylePlaceholderStart, StylePlaceholderEnd, styleDefinitions);
            content = UpdatePlaceholders(content, ScriptPlaceholderStart, ScriptPlaceholderEnd, scriptDefinitions);

            using var writer = new StreamWriter(htmlFilePath, false, fileEncoding);
            await writer.WriteAsync(content);
            await writer.FlushAsync();
        }

        private string UpdatePlaceholders(string content, string placeholderStart, string placeholderEnd, string definitions)
        {
            var placeholderStartIndex = content.IndexOf(placeholderStart);
            var placeholderEndIndex = content.IndexOf(placeholderEnd);
            var updatedContent = content.Remove(placeholderStartIndex, (placeholderEndIndex + placeholderEnd.Length) - placeholderStartIndex);
            return updatedContent.Insert(placeholderStartIndex, definitions);
        }

        private string GenerateStyleDefinitions(BundleContext context)
        {
            var builder = new StringBuilder();
            builder.AppendLine($"{StylePlaceholderStart}");
            foreach (var style in context.BundleDefinitions)
            {
                if (style.AdditionalProperties != null && style.AdditionalProperties.Any())
                {
                    builder.Append($"\t<link href=\"{style.Source}\" rel=\"stylesheet\" ");
                    foreach (var additionalProperty in style.AdditionalProperties)
                    {
                        builder.Append($"{additionalProperty.Key}={additionalProperty.Value} ");
                    }
                    builder.AppendLine("/>");
                }
                else
                {
                    builder.AppendLine($"\t<link href=\"{style.Source}\" rel=\"stylesheet\" />");
                }
            }
            builder.Append($"\t{StylePlaceholderEnd}");

            return builder.ToString();
        }

        private string GenerateScriptDefinitions(BundleContext context)
        {
            var builder = new StringBuilder();
            builder.AppendLine($"{ScriptPlaceholderStart}");
            foreach (var script in context.BundleDefinitions)
            {
                if (script.AdditionalProperties != null && script.AdditionalProperties.Any())
                {
                    builder.Append($"\t<script src=\"{script.Source}\" ");
                    foreach (var additionalProperty in script.AdditionalProperties)
                    {
                        builder.Append($"{additionalProperty.Key}={additionalProperty.Value} ");
                    }
                    builder.AppendLine("></script>");
                }
                else
                {
                    builder.AppendLine($"\t<script src=\"{script.Source}\"></script>");
                }
            }
            builder.Append($"\t{ScriptPlaceholderEnd}");

            return builder.ToString();
        }

        private IBundleContributer CreateContributerInstance(Type bundleContributerType)
        {
            var instance = Activator.CreateInstance(bundleContributerType);
            return instance.As<IBundleContributer>();
        }

        private void ExecuteBundleContributers(List<BundleTypeDefinition> bundleDefinitions, BundleContext styleContext, BundleContext scriptContext)
        {
            foreach (var bundleDefinition in bundleDefinitions)
            {
                var contributer = CreateContributerInstance(bundleDefinition.BundleContributerType);
                contributer.AddStyles(styleContext);
                contributer.AddScripts(scriptContext);
            }
        }

        private void FindBundleContributersRecursively(Type module, int level, List<BundleTypeDefinition> bundleDefinitions)
        {
            var dependencyDescriptors = module
                .GetCustomAttributes()
                .OfType<IDependedTypesProvider>();

            var bundleContributer = module.Assembly.GetTypes().SingleOrDefault(t => t.IsAssignableTo<IBundleContributer>());
            if (bundleContributer != null)
            {
                var definition = bundleDefinitions.SingleOrDefault(t => t.BundleContributerType == bundleContributer);
                if (definition != null)
                {
                    if (definition.Level < level)
                    {
                        definition.Level = level;
                    }
                }
                else
                {
                    bundleDefinitions.Add(new BundleTypeDefinition
                    {
                        Level = level,
                        BundleContributerType = bundleContributer
                    });
                }
            }

            foreach (var descriptor in dependencyDescriptors)
            {
                foreach (var dependedModuleType in descriptor.GetDependedTypes())
                {
                    FindBundleContributersRecursively(dependedModuleType, level + 1, bundleDefinitions);
                }
            }
        }

        private Type GetStartupModule(string assemblyPath)
        {
            var assembly = Assembly.LoadFrom(assemblyPath);
            return assembly.GetTypes().SingleOrDefault(IsAbpModule);

            static bool IsAbpModule(Type type)
            {
                var typeInfo = type.GetTypeInfo();

                return
                    typeInfo.IsClass &&
                    !typeInfo.IsAbstract &&
                    !typeInfo.IsGenericType &&
                    typeof(IAbpModule).GetTypeInfo().IsAssignableFrom(type);
            }
        }

        private string GetFrameworkFolderPath(string projectDirectory, string frameworkVersion)
        {
            return Path.Combine(projectDirectory, "bin", "Debug", frameworkVersion, "wwwroot", "_framework"); ;
        }

        private string GetTargetFrameworkVersion(string projectFilePath)
        {
            var document = new XmlDocument();
            document.Load(projectFilePath);
            var sdk = document.DocumentElement.GetAttribute("Sdk");
            if (sdk == SupportedWebAssemblyProjectType)
            {
                var frameworkVersion = document.SelectSingleNode("//TargetFramework").InnerText;
                return frameworkVersion;
            }
            else
            {
                throw new BundlingException($"Unsupported project type. Project type must be {SupportedWebAssemblyProjectType}.");
            }
        }

        private string GetAssemblyFilePath(string directory, string frameworkVersion, string projectFileName)
        {
            var outputDirectory = GetFrameworkFolderPath(directory, frameworkVersion);
            return Path.Combine(outputDirectory, projectFileName + ".dll");
        }
    }
}
