using System;
using System.IO;
using System.Linq;
using Microsoft.Build.Tasks;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tools.DotNet;
using Serilog;
using static Nuke.Common.EnvironmentInfo;
using static Nuke.Common.IO.PathConstruction;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

public partial class Build
{
    Target RestoreDesktopApp => _ => _
        .Before(CompileApp)
        .DependsOn(GenerateMetadata)
        .Executes(RestoreDesktopOrIosApp);

    Target CleanDesktopApp => _ => _
        .Before(CompileApp)
        .DependsOn(CleanOutputDir)
        .DependsOn(GenerateMetadata)
        .DependsOn(RestoreDesktopApp)
        .Executes(CleanDesktopOrIosApp);

    Target CompileApp => t => t
        .DependsOn(GenerateSecrets)
        .DependsOn(GenerateMetadata)
        .DependsOn(PopulateGitVersion)
        .DependsOn(CleanDesktopApp)
        .Executes(CompileDesktopOrIosApp);

    Target GenerateAppZipArchive => _ => _
        .Produces(AppPublishArtifactPath)
        .DependsOn(CompileApp)
        .OnlyWhenDynamic(() => Package != "deb" && Package != "pkg" && Package != "ipa")
        .Executes(() =>
        {
            AppPublishPath.ZipTo(AppPublishArtifactPath);
        });

    Target PublishApp => _ => _
        .DependsOn(CompileApp)
        .DependsOn(GenerateAppZipArchive)
        .DependsOn(PostCleanup);
}
