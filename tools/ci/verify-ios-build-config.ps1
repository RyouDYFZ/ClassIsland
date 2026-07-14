param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
)

$ErrorActionPreference = "Stop"

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Read-RepositoryFile {
    param([string]$RelativePath)

    $path = Join-Path $RepositoryRoot $RelativePath
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "Missing repository file: $RelativePath"
    return Get-Content -LiteralPath $path -Raw
}

$iosProjectText = Read-RepositoryFile "ClassIsland.iOS/ClassIsland.iOS.csproj"
$iosProject = [xml]$iosProjectText
$nativeProjectText = Read-RepositoryFile "ClassIsland.iOS.Native/ClassIsland.iOS.Native.xcodeproj/project.pbxproj"
$infoPlistText = Read-RepositoryFile "ClassIsland.iOS/Info.plist"
$infoPlist = [xml]$infoPlistText
$privacyManifestText = Read-RepositoryFile "ClassIsland.iOS/PrivacyInfo.xcprivacy"
$privacyManifest = [xml]$privacyManifestText
$releaseWorkflowText = Read-RepositoryFile ".github/workflows/build_release.yml"
$ipaVerificationText = Read-RepositoryFile "tools/ci/verify-ios-ipa.sh"
$coverageVerificationText = Read-RepositoryFile "tools/ci/verify-cobertura-coverage.ps1"
$coverageSettingsText = Read-RepositoryFile "ClassIsland.Platforms.Abstractions.Tests/coverlet.runsettings"
$coverageSettings = [xml]$coverageSettingsText
$artifactInitializationText = Read-RepositoryFile "tools/release-gen/init-artifacts.ps1"
$nukeBuildText = Read-RepositoryFile "build/Build.App.cs"
$nukeSchema = Read-RepositoryFile ".nuke/build.schema.json" | ConvertFrom-Json
$liveActivityServiceText = Read-RepositoryFile "ClassIsland.iOS/Services/LiveActivities/IosLiveActivityService.cs"

$supportedVersion = $iosProject.SelectSingleNode("/Project/PropertyGroup/SupportedOSPlatformVersion").InnerText
Assert-True ($supportedVersion -eq "15.0") "The iOS app minimum supported version must be 15.0."

$runtimeIdentifier = $iosProject.SelectSingleNode("/Project/PropertyGroup/RuntimeIdentifier").InnerText
Assert-True ($runtimeIdentifier -eq "ios-arm64") "The iOS project must default to the ios-arm64 device RID."
$runtimeValidationTarget = $iosProject.SelectSingleNode('/Project/Target[@Name="ValidateIosDeviceRuntime"]')
Assert-True ($null -ne $runtimeValidationTarget) "The iOS project must explicitly reject Simulator RIDs."
$runtimeValidationConditions = @($runtimeValidationTarget.Error) | ForEach-Object { $_.Condition }
Assert-True ($runtimeValidationConditions -contains "'`$(RuntimeIdentifier)' != 'ios-arm64'") "Single-RID validation must allow only ios-arm64."
Assert-True (($runtimeValidationConditions -join "`n").Contains("Copy('`$(RuntimeIdentifiers)').Contains('iossimulator-')")) "Multi-RID validation must reject iossimulator-* entries."

$releaseSymbolGroup = @($iosProject.Project.PropertyGroup) |
    Where-Object {
        $_.Condition -eq "'`$(Configuration)' == 'Release'" -and
        $_.DebugType -eq "none" -and
        $_.DebugSymbols -eq "false"
    } |
    Select-Object -First 1
Assert-True ($null -ne $releaseSymbolGroup) "The iOS Release project must disable debug symbols after shared props imports."

$derivedData = $iosProject.SelectSingleNode("/Project/PropertyGroup/ClassIslandLiveActivityDerivedData").InnerText
Assert-True ($derivedData -like '*$(MSBuildProjectDirectory)/obj/*') "Xcode DerivedData must be stored under ClassIsland.iOS/obj."
Assert-True (-not $iosProjectText.Contains('$(IntermediateOutputPath)xcode-live-activity-extension')) "DerivedData must not depend on an early IntermediateOutputPath evaluation."

$soundFlowTarget = $iosProject.SelectSingleNode('/Project/Target[@Name="CreateSoundFlowIosResolverAlias"]')
Assert-True ($null -ne $soundFlowTarget) "The production build is missing the SoundFlow resolver target."
Assert-True ($soundFlowTarget.AfterTargets -eq "_CreateAppBundle") "The SoundFlow resolver must run after the app bundle is created."
Assert-True ($soundFlowTarget.BeforeTargets -eq "CoreCodesign") "The SoundFlow resolver must run before app signing."
Assert-True ($soundFlowTarget.Exec.Command.Contains("../../../../Frameworks/miniaudio.framework/miniaudio")) "The SoundFlow resolver target path is incorrect."

$swiftRuntimeTarget = $iosProject.SelectSingleNode('/Project/Target[@Name="EmbedIosSwiftRuntimeLibraries"]')
Assert-True ($null -ne $swiftRuntimeTarget) "Swift runtime embedding must be part of the production app-bundle build."
Assert-True ($swiftRuntimeTarget.AfterTargets -eq "_CreateAppBundle") "Swift runtime embedding must run after the app bundle is created."
Assert-True ($swiftRuntimeTarget.BeforeTargets -eq "CoreCodesign") "Swift runtime embedding must run before app signing."
$swiftRuntimeSignArguments = $swiftRuntimeTarget.SelectSingleNode("PropertyGroup/_IosSwiftRuntimeSignArguments")
Assert-True ($null -ne $swiftRuntimeSignArguments) "Signed builds must configure swift-stdlib-tool signing arguments."
Assert-True ($swiftRuntimeSignArguments.Condition -eq "'`$(EnableCodeSigning)' == 'true'") "Swift runtime signing arguments must be enabled only for signed builds."
Assert-True ($swiftRuntimeSignArguments.InnerText -eq '--sign "$(CodesignKey)"') "Swift runtime libraries must be signed with CodesignKey."
Assert-True ($swiftRuntimeTarget.Exec.Command.Contains("swift-stdlib-tool --copy `$(_IosSwiftRuntimeSignArguments) --platform iphoneos")) "The Swift runtime target must invoke swift-stdlib-tool with conditional signing arguments."
Assert-True (-not $swiftRuntimeTarget.Exec.Command.Contains("--sign")) "Unsigned builds must not pass a literal --sign argument to swift-stdlib-tool."
$swiftRuntimeErrorConditions = @($swiftRuntimeTarget.Error) | ForEach-Object { $_.Condition }
Assert-True ($swiftRuntimeErrorConditions -contains "'`$(EnableCodeSigning)' == 'true' And '`$(CodesignKey)' == ''") "Signed Swift runtime embedding must require CodesignKey."

$derivedDataCleanTarget = $iosProject.SelectSingleNode('/Project/Target[@Name="CleanClassIslandLiveActivityDerivedData"]')
Assert-True ($null -ne $derivedDataCleanTarget) "The iOS Clean target must remove Xcode DerivedData."

$ios15Targets = ([regex]::Matches($nativeProjectText, "IPHONEOS_DEPLOYMENT_TARGET = 15\.0;")).Count
$ios161Targets = ([regex]::Matches($nativeProjectText, "IPHONEOS_DEPLOYMENT_TARGET = 16\.1;")).Count
Assert-True ($ios15Targets -eq 4) "The Xcode project and bridge Debug/Release targets must use iOS 15.0."
Assert-True ($ios161Targets -eq 2) "The Live Activity extension Debug/Release targets must remain on iOS 16.1."
Assert-True (-not $nativeProjectText.Contains("IPHONEOS_DEPLOYMENT_TARGET = 13.0;")) "The Xcode project still contains an iOS 13.0 target unsupported by Xcode 26."
Assert-True (-not $nativeProjectText.Contains("iphonesimulator")) "The native bridge and extension must not declare Simulator platforms."

$plistKeys = @($infoPlist.plist.dict.key)
Assert-True (-not ($plistKeys -contains "CFBundleDisplayName")) "Info.plist must not hard-code CFBundleDisplayName; ApplicationTitle must generate it."
Assert-True ($liveActivityServiceText.Contains('[SupportedOSPlatform("ios15.0")]')) "The managed Live Activity bridge must declare the iOS 15.0 platform floor."

$privacyBundleResource = $iosProject.SelectSingleNode('/Project/ItemGroup/BundleResource[@Include="PrivacyInfo.xcprivacy"]')
Assert-True ($null -ne $privacyBundleResource) "The iOS project must package PrivacyInfo.xcprivacy as a bundle resource."
Assert-True ($privacyBundleResource.GetAttribute("LogicalName") -eq "PrivacyInfo.xcprivacy") "The app privacy manifest must be placed at the root of the app bundle."
$privacyUserDefaultsType = $privacyManifest.SelectSingleNode("/plist/dict/key[.='NSPrivacyAccessedAPITypes']/following-sibling::array[1]/dict/key[.='NSPrivacyAccessedAPIType']/following-sibling::string[1][.='NSPrivacyAccessedAPICategoryUserDefaults']")
$privacyUserDefaultsReason = $privacyManifest.SelectSingleNode("/plist/dict/key[.='NSPrivacyAccessedAPITypes']/following-sibling::array[1]/dict/key[.='NSPrivacyAccessedAPITypeReasons']/following-sibling::array[1]/string[.='CA92.1']")
Assert-True ($null -ne $privacyUserDefaultsType) "PrivacyInfo.xcprivacy must declare NSUserDefaults as a required-reason API."
Assert-True ($null -ne $privacyUserDefaultsReason) "PrivacyInfo.xcprivacy must use the CA92.1 app-only NSUserDefaults reason."

$standaloneWorkflowPath = Join-Path $RepositoryRoot ".github/workflows/build_ios.yml"
$reusableWorkflowPath = Join-Path $RepositoryRoot ".github/workflows/_build_ios_reusable.yml"
Assert-True (-not (Test-Path -LiteralPath $standaloneWorkflowPath)) "The split Build iOS workflow must remain removed."
Assert-True (-not (Test-Path -LiteralPath $reusableWorkflowPath)) "The split reusable iOS worker must remain removed."
Assert-True ($releaseWorkflowText.Contains("pull_request:")) "The unified Build workflow must validate iOS changes on pull requests."
Assert-True ($releaseWorkflowText.Contains("github.ref == 'refs/heads/develop/v2/misha-alpha'")) "The unified Build workflow must validate pushes to develop/v2/misha-alpha."
Assert-True ($releaseWorkflowText.Contains("startsWith(github.ref, 'refs/tags/ios-v')")) "The unified Build workflow must validate ios-v* tags."
Assert-True ($releaseWorkflowText.Contains('ref: ${{ github.event_name == ''workflow_dispatch'' && inputs.release_tag || github.sha }}')) "The iOS job must build the release tag for dispatches and the triggering commit otherwise."
Assert-True ($releaseWorkflowText.Contains('app_version="${GITHUB_REF_NAME#ios-v}"')) "An ios-v* tag must supply the IPA display version."
Assert-True ($releaseWorkflowText.Contains('app_version="0.0.${GITHUB_RUN_NUMBER}"')) "PR and branch builds must use a valid synthetic iOS display version."
Assert-True ($releaseWorkflowText.Contains("developer_preview=true")) "PR and branch iOS builds must enable DeveloperPreview."
Assert-True ($releaseWorkflowText.Contains("developer_preview=false")) "Release dispatch and ios-v* tag builds must disable DeveloperPreview."
Assert-True ($releaseWorkflowText.Contains("runs-on: macos-26")) "The unified iOS job must run on the supported macOS runner."
Assert-True ($releaseWorkflowText.Contains("NUGET_AUTH_TOKEN: `${{ github.token }}")) "The iOS job must use its scoped GITHUB_TOKEN for package restore."
Assert-True ($releaseWorkflowText.Contains("packages: read")) "The iOS job must request read-only package access."
Assert-True ($releaseWorkflowText.Contains("IOS_CONFIGURATION: Release")) "The unsigned IPA must use a Release app build."
Assert-True ($releaseWorkflowText.Contains("./build.sh PublishApp")) "The iOS job must build through the shared NUKE PublishApp target."
Assert-True ($releaseWorkflowText.Contains('DeveloperPreview: ${{ steps.metadata.outputs.developer_preview }}')) "The NUKE iOS build must receive the resolved DeveloperPreview mode."
Assert-True ($releaseWorkflowText.Contains("enableCodeSigning: 'false'")) "The unified iOS job must produce an unsigned IPA."
Assert-True (-not $releaseWorkflowText.Contains("Add SoundFlow iOS resolver alias")) "The SoundFlow resolver fix must not exist only in CI shell code."
Assert-True (-not $releaseWorkflowText.Contains("Embed Swift runtime libraries")) "Swift runtime embedding must not exist only in CI shell code."
Assert-True ($releaseWorkflowText.Contains("coverlet.runsettings")) "The iOS job must apply coverlet.runsettings."
Assert-True ($releaseWorkflowText.Contains("XPlat Code Coverage")) "The iOS job must enable the coverlet collector."
Assert-True ($releaseWorkflowText.Contains("verify-cobertura-coverage.ps1")) "The iOS job must enforce the coverage threshold."
Assert-True ($releaseWorkflowText.Contains("-MinimumLineRate 0.8")) "The iOS job must enforce at least 80% line coverage."
Assert-True ($releaseWorkflowText.Contains("bash ./tools/ci/verify-ios-ipa.sh")) "The iOS job must run the shared IPA verification script."
Assert-True ($releaseWorkflowText.Contains("name: out_app_ios_arm64_selfContained_ipa")) "The iOS artifact must use the release collector naming convention."
Assert-True ($releaseWorkflowText.Contains("needs: [ pack_app, build_nupkg, build_ios_unsigned ]")) "Publishing must depend on the iOS build."
Assert-True ($releaseWorkflowText.Contains("needs.build_ios_unsigned.result == 'success'")) "A failed or skipped iOS build must block publishing."
Assert-True ($releaseWorkflowText.Contains("Verify downloaded iOS release artifact")) "Publishing must validate the downloaded IPA and checksum."
Assert-True ($releaseWorkflowText.Contains("./out/*.ipa,./out/*.sha256")) "The draft release must upload the IPA and portable checksum."

Assert-True ($coverageVerificationText.Contains('GetAttribute("line-rate")')) "Coverage verification must read the Cobertura root line rate."
Assert-True ($coverageVerificationText.Contains('$lineRate -lt $MinimumLineRate')) "Coverage verification must fail below the requested threshold."
Assert-True ($coverageVerificationText.Contains('"Stubs/Services/AvaloniaDefaultPlatformFilePickerService.cs"')) "Coverage verification must require the platform file-picker implementation."
$coverageInclude = $coverageSettings.SelectSingleNode("/RunSettings/DataCollectionRunSettings/DataCollectors/DataCollector/Configuration/Include").InnerText
$coverageExclusions = $coverageSettings.SelectSingleNode("/RunSettings/DataCollectionRunSettings/DataCollectors/DataCollector/Configuration/ExcludeByFile").InnerText
Assert-True ($coverageInclude -eq "[ClassIsland.Platforms.Abstractions]*") "Coverage must instrument the complete platform abstraction assembly."
Assert-True ($coverageExclusions.Contains("AppLifetimeServiceStub.cs")) "Only explicit no-op platform stubs should be excluded from abstraction coverage."
Assert-True (-not $coverageSettingsText.Contains("ClassIsland.Platforms.Abstraction.Models.LiveActivities.*,")) "Coverage must not regress to a hand-maintained type whitelist."

Assert-True ($artifactInitializationText.Contains('Contains("out_app_")')) "Release artifact initialization must collect the normalized iOS artifact."
Assert-True (-not $artifactInitializationText.Contains("ClassIsland-iOS-unsigned")) "Release artifact initialization must not filter out the unsigned iOS artifact."
Assert-True ($artifactInitializationText.Contains('$payloadName')) "Release checksums must reference the renamed payload by basename."

Assert-True ($ipaVerificationText.Contains("<ipa-path> <application-id> <runtime-identifier>")) "IPA verification must document its three required arguments."
Assert-True ($ipaVerificationText.Contains("MonoTouchDebugConfiguration.txt")) "IPA verification must reject MonoTouch debug configuration."
Assert-True ($ipaVerificationText.Contains("libxamarin-dotnet-debug")) "IPA verification must reject the remote-debug runtime."
Assert-True ($ipaVerificationText.Contains('assert_minimum_ios "$app_binary" "The main app" "15.0"')) "IPA verification must assert iOS 15.0 for the main app."
Assert-True ($ipaVerificationText.Contains('assert_minimum_ios "$bridge_binary" "The Live Activity bridge" "15.0"')) "IPA verification must assert iOS 15.0 for the bridge."
Assert-True ($ipaVerificationText.Contains("CFBundleDisplayName")) "IPA verification must validate the final display name."
Assert-True ($ipaVerificationText.Contains("assert_unsigned_bundle")) "IPA verification must reject signed app, extension, and bridge bundles."
Assert-True ($ipaVerificationText.Contains("PrivacyInfo.xcprivacy")) "IPA verification must require the packaged app privacy manifest."
Assert-True ($ipaVerificationText.Contains("NSPrivacyAccessedAPICategoryUserDefaults")) "IPA verification must validate the NSUserDefaults required-reason declaration."
Assert-True ($ipaVerificationText.Contains("-name '*.xpc' -o -name '*.bundle'")) "IPA verification must inspect nested XPC and resource bundles."
Assert-True ($ipaVerificationText.Contains("-name '_CodeSignature'")) "IPA verification must reject signing metadata anywhere in the app bundle."
Assert-True ($ipaVerificationText.Contains('shasum -a 256 "$ipa_basename"')) "IPA verification must generate a portable basename-only checksum."

$publishBuildingPropertyCount = ([regex]::Matches($nukeBuildText, [regex]::Escape('SetProperty("PublishBuilding", true)'))).Count
$iosPublishPlatformPropertyCount = ([regex]::Matches($nukeBuildText, [regex]::Escape('SetProperty("PublishPlatform", "ios")'))).Count
$iosArchitecturePropertyCount = ([regex]::Matches($nukeBuildText, [regex]::Escape('SetProperty("ClassIsland_PlatformTarget", "arm64")'))).Count
Assert-True ($publishBuildingPropertyCount -ge 6) "NUKE restore, clean, and publish paths must all mark iOS as a publish build."
Assert-True ($iosPublishPlatformPropertyCount -ge 3) "NUKE restore, clean, and publish paths must all select the iOS publish platform."
Assert-True ($iosArchitecturePropertyCount -ge 3) "NUKE restore, clean, and publish paths must all define the arm64 ClassIsland platform target."
Assert-True ($nukeBuildText.Contains('SetProperty("EnableCodeSigning", enableCodeSigning)')) "NUKE iOS publish must explicitly support signed and unsigned modes."
Assert-True ($nukeBuildText.Contains('SetProperty("ArchiveOnBuild", enableCodeSigning)')) "Unsigned NUKE publish must skip xcarchive creation while still building the IPA."
Assert-True ($nukeBuildText.Contains('SetProperty("BuildIpa", true)')) "NUKE iOS publish must keep IPA packaging enabled."
Assert-True ($nukeBuildText.Contains('SetProperty("DebugType", "none")')) "NUKE iOS Release publish must disable PDB generation for every project reference."
Assert-True ($nukeBuildText.Contains('SetProperty("DebugSymbols", false)')) "NUKE iOS Release publish must disable debug symbols for every project reference."
$enableCodeSigningSchema = @($nukeSchema.allOf) |
    ForEach-Object { $_.properties.EnableCodeSigning } |
    Where-Object { $null -ne $_ } |
    Select-Object -First 1
Assert-True ($enableCodeSigningSchema.type -eq "boolean") "The NUKE schema must expose EnableCodeSigning as a boolean parameter."

Write-Output "iOS build configuration verification passed."
