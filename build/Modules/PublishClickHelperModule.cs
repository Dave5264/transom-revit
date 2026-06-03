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
///     Publish the self-contained Click Helper executables for the optional Claude UI-Assist feature —
///     the MCP server (<c>Transom.ClickHelper.Mcp.exe</c>) and the UI-automation engine it drives
///     (<c>Transom.ClickHelper.exe</c>) — and bundle BOTH into every add-in publish folder so the
///     installer ships them next to Transom.dll. Mirrors <see cref="PublishShimModule"/>.
/// </summary>
[DependsOn<CompileProjectModule>]
public sealed class PublishClickHelperModule : Module
{
    protected override async Task ExecuteModuleAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        // Both pieces are self-contained single-file (each csproj pins PublishSingleFile / SelfContained /
        // RuntimeIdentifier=win-x64 in Release), so configuration is the only flag we drive here.
        var projects = new[]
        {
            new File(Projects.Transom_ClickHelper_Mcp.FullName),
            new File(Projects.Transom_ClickHelper.FullName),
        };

        // The add-in publish folders the installer's "{dir}\*.*" glob ships from.
        var addInTarget = new File(Projects.Transom.FullName);
        var publishFolders = addInTarget.Folder!
            .GetFolder("bin")
            .GetFolders(folder => folder.Name == "publish")
            .ToArray();

        publishFolders.ShouldNotBeEmpty("No add-in publish folders were found to bundle the Click Helper into");

        foreach (var project in projects)
        {
            await context.DotNet().Publish(new DotNetPublishOptions
            {
                ProjectSolution = project.Path,
                Configuration = "Release",
                Runtime = "win-x64"
            }, cancellationToken: cancellationToken);

            // Locate the published single-file executable inside this project's "publish" folder.
            var exe = project.Folder!
                .GetFolder("bin")
                .FindFile(file => file.NameWithoutExtension == project.NameWithoutExtension
                                  && file.Extension == ".exe"
                                  && file.Folder?.Name == "publish");

            exe.ShouldNotBeNull($"No published executable was found for {project.NameWithoutExtension}");

            foreach (var publishFolder in publishFolders)
            {
                exe.CopyTo(publishFolder.GetFile(exe.Name).Path);
            }
        }
    }
}
