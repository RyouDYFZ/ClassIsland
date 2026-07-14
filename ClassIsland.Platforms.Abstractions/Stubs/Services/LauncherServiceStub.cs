using ClassIsland.Platforms.Abstraction.Services;

namespace ClassIsland.Platforms.Abstraction.Stubs.Services;

/// <summary>
/// 不执行外部启动操作的兼容桩服务。
/// </summary>
public class LauncherServiceStub : ILauncherService
{
    /// <inheritdoc />
    public Task LaunchPath(string path) => Task.CompletedTask;

    /// <inheritdoc />
    public Task LaunchUrl(string url) => Task.CompletedTask;
}
