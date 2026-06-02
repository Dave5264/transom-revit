using ModularPipelines.Attributes;
using ModularPipelines.Context;
using ModularPipelines.DotNet.Extensions;
using ModularPipelines.DotNet.Options;
using ModularPipelines.FileSystem;
using ModularPipelines.Modules;
using Shouldly;
using Sourcy.DotNet;
using File = ModularPipelines.FileSystem.File;

namespace Build.Modules;

/// <summary>
///     Publish the self-contained, single-file MCP shim and bundle its executable
///     into every add-in publish folder so the installer ships it.
/// </summary>
[DependsOn<CompileProjectModule>]
public sealed class PublishShimModule : Module
{
    protected override async Task ExecuteModuleAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        var shimProject = new File(Projects.Transom_McpShim.FullName);

        // Publish the shim self-contained for win-x64. The csproj pins
        // PublishSingleFile / SelfContained / RuntimeIdentifier=win-x64, so the
        // configuration is the only flag we need to drive here.
        await context.DotNet().Publish(new DotNetPublishOptions
        {
            ProjectSolution = shimProject.Path,
            Configuration = "Release",
            Runtime = "win-x64"
        }, cancellationToken: cancellationToken);

        // Locate the published single-file executable inside a "publish" folder.
        var shimExe = shimProject.Folder!
            .GetFolder("bin")
            .FindFile(file => file.NameWithoutExtension == shimProject.NameWithoutExtension
                              && file.Extension == ".exe"
                              && file.Folder?.Name == "publish");

        shimExe.ShouldNotBeNull($"No published executable was found for the MCP shim: {shimProject.NameWithoutExtension}");

        // Copy the shim next to Transom.dll in every per-version add-in publish
        // folder so the installer's existing "{dir}\*.*" glob bundles it.
        var addInTarget = new File(Projects.Transom.FullName);
        var publishFolders = addInTarget.Folder!
            .GetFolder("bin")
            .GetFolders(folder => folder.Name == "publish")
            .ToArray();

        publishFolders.ShouldNotBeEmpty("No add-in publish folders were found to bundle the MCP shim into");

        foreach (var publishFolder in publishFolders)
        {
            shimExe.CopyTo(publishFolder.GetFile(shimExe.Name).Path);
        }
    }
}
