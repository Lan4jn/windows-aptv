using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Iptv.Core.Models;
using Iptv.Desktop;
using Iptv.Desktop.Services;
using Iptv.Desktop.ViewModels;
using Xunit;

namespace Iptv.Desktop.Tests.Integration;

public sealed class CommandErrorRegressionTests
{
    private sealed class CapturingSynchronizationContext : SynchronizationContext
    {
        public readonly List<Exception> PostedExceptions = new();

        public override void Post(SendOrPostCallback d, object? state)
        {
            try
            {
                d(state);
            }
            catch (Exception ex)
            {
                PostedExceptions.Add(ex);
            }
        }
    }

    private sealed class FailingLibraryService : ILibraryService
    {
        private readonly FakeLibraryService _inner = new();

        public Task<ProxyPolicyContext> GetProxyPolicyContextAsync(CancellationToken ct = default) => _inner.GetProxyPolicyContextAsync(ct);
        public Task<IReadOnlyList<UserProxy>> GetUserProxiesAsync(CancellationToken ct = default) => _inner.GetUserProxiesAsync(ct);
        public Task SaveUserProxyAsync(UserProxy proxy, CancellationToken ct = default) => _inner.SaveUserProxyAsync(proxy, ct);
        public Task DeleteUserProxyAsync(string proxyId, CancellationToken ct = default) => _inner.DeleteUserProxyAsync(proxyId, ct);
        public Task SetSourceProxyAsync(string sourceId, string? proxyId, CancellationToken ct = default) => _inner.SetSourceProxyAsync(sourceId, proxyId, ct);
        public Task<IReadOnlyList<SourceConfig>> GetConfigsAsync(CancellationToken ct = default) => _inner.GetConfigsAsync(ct);
        public Task SaveConfigAsync(SourceConfig config, CancellationToken ct = default) => Task.FromException(new IOException("Simulated disk error"));
        public Task SaveSourceOrderAsync(IReadOnlyList<string> ids, CancellationToken ct = default) => Task.FromException(new IOException("Simulated disk error"));
        public Task DeleteConfigAsync(string configId, CancellationToken ct = default) => _inner.DeleteConfigAsync(configId, ct);
        public Task<IReadOnlyList<Channel>> GetFavoritesAsync(CancellationToken ct = default) => _inner.GetFavoritesAsync(ct);
        public Task AddFavoriteAsync(Channel channel, CancellationToken ct = default) => _inner.AddFavoriteAsync(channel, ct);
        public Task RemoveFavoriteAsync(string channelId, CancellationToken ct = default) => _inner.RemoveFavoriteAsync(channelId, ct);
        public Task<bool> IsFavoriteAsync(string channelId, CancellationToken ct = default) => _inner.IsFavoriteAsync(channelId, ct);
        public Task<IReadOnlyList<Channel>> GetHistoryAsync(CancellationToken ct = default) => _inner.GetHistoryAsync(ct);
        public Task AddHistoryAsync(Channel channel, CancellationToken ct = default) => _inner.AddHistoryAsync(channel, ct);
        public Task ClearHistoryAsync(CancellationToken ct = default) => _inner.ClearHistoryAsync(ct);
        public Task<IReadOnlyList<Channel>> GetAllChannelsAsync(CancellationToken ct = default) => _inner.GetAllChannelsAsync(ct);
    }

    [Fact]
    public void Execute_NonCancellationException_DoesNotEscapeToSynchronizationContext()
    {
        var originalContext = SynchronizationContext.Current;
        var capturingContext = new CapturingSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(capturingContext);

        try
        {
            var command = new AsyncRelayCommand(() => Task.FromException(new IOException("Simulated disk error")));
            ((ICommand)command).Execute(null);

            // Give any posted delegate an opportunity to run
            Assert.Empty(capturingContext.PostedExceptions);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(originalContext);
        }
    }

    [Fact]
    public void Execute_WhenReporterThrows_DoesNotEscapeToSynchronizationContextAndSubsequentSubscribersNotified()
    {
        var originalContext = SynchronizationContext.Current;
        var capturingContext = new CapturingSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(capturingContext);

        try
        {
            bool secondSubscriberNotified = false;
            var command = new AsyncRelayCommand(
                () => Task.FromException(new IOException("Original error")),
                onError: _ => throw new IOException("reporterfailed in onError"));

            command.CommandFailed += (s, e) => throw new IOException("reporterfailed in event 1");
            command.CommandFailed += (s, e) => secondSubscriberNotified = true;

            ((ICommand)command).Execute(null);

            Assert.Empty(capturingContext.PostedExceptions);
            Assert.True(secondSubscriberNotified);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(originalContext);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsException_ResetsIsExecutingAndFiresErrorNotification()
    {
        Exception? caughtException = null;
        var command = new AsyncRelayCommand(
            () => Task.FromException(new IOException("Disk error")),
            onError: ex => caughtException = ex);

        Assert.True(command.CanExecute(null));

        await command.ExecuteAsync(null);

        Assert.False(command.IsExecuting);
        Assert.True(command.CanExecute(null));
        Assert.NotNull(caughtException);
        Assert.IsType<IOException>(caughtException);
    }

    [Fact]
    public async Task ExecuteAsync_OperationCanceledException_ResetsIsExecutingWithoutReportingError()
    {
        Exception? caughtException = null;
        var command = new AsyncRelayCommand(
            () => Task.FromCanceled(new CancellationToken(true)),
            onError: ex => caughtException = ex);

        await command.ExecuteAsync(null);

        Assert.False(command.IsExecuting);
        Assert.True(command.CanExecute(null));
        Assert.Null(caughtException);
    }

    [Fact]
    public void Execute_SensitiveException_DoesNotLeakRawSecretToMainWindowViewModel()
    {
        var failingLib = new FailingLibraryService();
        var vm = new MainWindowViewModel(new CorePlaylistService(), failingLib, new CoreEpgService(), new FakePlayer());

        // Fill form with sensitive data
        vm.ConfigsVm.OpenAddM3uDialogCommand.Execute(null);
        vm.ConfigsVm.AddForm.Name = "Test Source";
        vm.ConfigsVm.AddForm.LocationUrl = "https://secret.server/playlist.m3u?token=SUPER_SECRET_TOKEN_XYZ123";

        // Execute save command which will fail via FailingLibraryService
        ((ICommand)vm.ConfigsVm.SaveAddFormCommand).Execute(null);

        Assert.NotNull(vm.ErrorMessage);
        Assert.DoesNotContain("SUPER_SECRET_TOKEN_XYZ123", vm.ErrorMessage);
        Assert.DoesNotContain("token=", vm.ErrorMessage);
        Assert.Contains("本地文件或存储读写失败", vm.ErrorMessage);
    }

    [Fact]
    public void Execute_CrossVm_IndependentInstancesDoNotPolluteEachOther()
    {
        var failingLib = new FailingLibraryService();
        var vm1 = new MainWindowViewModel(new CorePlaylistService(), failingLib, new CoreEpgService(), new FakePlayer());
        var vm2 = new MainWindowViewModel(new CorePlaylistService(), new MemoryOnlyLibraryService(), new CoreEpgService(), new FakePlayer());

        // An unhandled error on an unrelated standalone command must NOT affect vm1 or vm2
        var standaloneCommand = new AsyncRelayCommand(() => Task.FromException(new IOException("Standalone error")));
        ((ICommand)standaloneCommand).Execute(null);

        Assert.Null(vm1.ErrorMessage);
        Assert.Null(vm2.ErrorMessage);

        // Fail a command owned by vm1
        vm1.ConfigsVm.OpenAddM3uDialogCommand.Execute(null);
        vm1.ConfigsVm.AddForm.Name = "Test Source";
        vm1.ConfigsVm.AddForm.LocationUrl = "https://example.com/test.m3u";
        ((ICommand)vm1.ConfigsVm.SaveAddFormCommand).Execute(null);

        // vm1 must receive error, but vm2 must remain unpolluted!
        Assert.NotNull(vm1.ErrorMessage);
        Assert.Null(vm2.ErrorMessage);
    }

    [AvaloniaFact]
    public void HeadlessRoot_ErrorBanner_IsVisibleWhenErrorMessageIsSet()
    {
        var vm = new MainWindowViewModel(new CorePlaylistService(), new MemoryOnlyLibraryService(), new CoreEpgService(), new FakePlayer());
        var window = new MainWindow { DataContext = vm };
        window.Show();

        var banner = window.FindControl<Border>("RootErrorBanner");
        var errorTextBlock = window.FindControl<TextBlock>("RootErrorTextBlock");
        Assert.NotNull(banner);
        Assert.NotNull(errorTextBlock);
        Assert.False(banner.IsVisible);

        vm.ErrorMessage = "测试通用错误信息";
        Assert.True(banner.IsVisible);
        Assert.Equal("测试通用错误信息", errorTextBlock.Text);

        vm.DismissErrorCommand.Execute(null);
        Assert.Null(vm.ErrorMessage);
        Assert.False(banner.IsVisible);

        window.Close();
    }
}
