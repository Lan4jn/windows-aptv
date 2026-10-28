using Iptv.Core.Models;
using Iptv.Desktop.Services;

namespace Iptv.Desktop.Tests.Services;

public sealed class PlayerProxyPreflightTests
{
    private static Channel Channel(string id = "a") => new(id, "s", "synthetic", "http://origin.invalid/fixture");
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task NativeStartWaitsUntilPreflightCompletes()
    {
        var release = Signal();
        var starts = 0;
        using var session = new LibVlcPlayerSession(_ => { starts++; return true; }, (_, _) => release.Task);
        var pending = session.PlayAsync(Channel());
        Assert.Equal(0, starts);
        Assert.Equal(PlayerState.Loading, session.State);
        release.SetResult();
        await pending;
        Assert.Equal(1, starts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopInvalidatesPreflightEvenWhenItIgnoresCancellation(bool lateFailure)
    {
        var release = Signal();
        var starts = 0;
        CancellationToken observed = default;
        using var session = new LibVlcPlayerSession(_ => { starts++; return true; }, (_, token) => { observed = token; return release.Task; });
        var pending = session.PlayAsync(Channel());
        await session.StopAsync();
        Assert.True(observed.IsCancellationRequested);
        if (lateFailure) release.SetException(new InvalidOperationException("synthetic-secret"));
        else release.SetResult();
        await pending;
        Assert.Equal(0, starts);
        Assert.Equal(PlayerState.Stopped, session.State);
        Assert.Null(session.CurrentChannel);
        Assert.Null(session.ErrorMessage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeInvalidatesPreflightWithoutLateStateMutation(bool lateFailure)
    {
        var release = Signal();
        var starts = 0;
        CancellationToken observed = default;
        var session = new LibVlcPlayerSession(_ => { starts++; return true; }, (_, token) => { observed = token; return release.Task; });
        var pending = session.PlayAsync(Channel());
        session.Dispose();
        var state = session.State;
        var current = session.CurrentChannel;
        Assert.True(observed.IsCancellationRequested);
        if (lateFailure) release.SetException(new InvalidOperationException("synthetic-secret"));
        else release.SetResult();
        await pending;
        Assert.Equal(0, starts);
        Assert.Equal(state, session.State);
        Assert.Same(current, session.CurrentChannel);
        Assert.Null(session.ErrorMessage);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NewPlayMakesLateOldPreflightIrrelevant(bool newFailure, bool oldFailure)
    {
        var release = Signal();
        var started = new List<string>();
        CancellationToken oldToken = default;
        using var session = new LibVlcPlayerSession(channel => { started.Add(channel.Id); return true; }, (channel, token) =>
        {
            if (channel.Id == "a") { oldToken = token; return release.Task; }
            return newFailure ? Task.FromException(new InvalidOperationException("new-private")) : Task.CompletedTask;
        });
        var old = session.PlayAsync(Channel());
        var next = Channel("b");
        await session.PlayAsync(next);
        var state = session.State;
        var error = session.ErrorMessage;
        Assert.True(oldToken.IsCancellationRequested);
        if (oldFailure) release.SetException(new InvalidOperationException("old-private"));
        else release.SetResult();
        await old;
        Assert.Equal(newFailure ? Array.Empty<string>() : new[] { "b" }, started);
        Assert.Same(next, session.CurrentChannel);
        Assert.Equal(state, session.State);
        Assert.Equal(error, session.ErrorMessage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellationCannotStartAfterAwait(bool cooperative)
    {
        var release = Signal();
        var entered = Signal();
        var starts = 0;
        using var caller = new CancellationTokenSource();
        using var session = new LibVlcPlayerSession(_ => { starts++; return true; }, (_, token) =>
        {
            entered.SetResult();
            return cooperative ? Task.Delay(Timeout.InfiniteTimeSpan, token) : release.Task;
        });
        var pending = session.PlayAsync(Channel(), caller.Token);
        await entered.Task;
        caller.Cancel();
        release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, starts);
        Assert.Equal(PlayerState.Stopped, session.State);
        Assert.Null(session.CurrentChannel);
        Assert.Null(session.ErrorMessage);
    }

    [Fact]
    public async Task CallerCancelledPreflightLateFailureDoesNotSetError()
    {
        var release = Signal();
        using var caller = new CancellationTokenSource();
        using var session = new LibVlcPlayerSession(_ => true, (_, _) => release.Task);
        var pending = session.PlayAsync(Channel(), caller.Token);
        caller.Cancel();
        release.SetException(new InvalidOperationException("synthetic-secret"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(PlayerState.Stopped, session.State);
        Assert.Null(session.CurrentChannel);
        Assert.Null(session.ErrorMessage);
    }

    [Fact]
    public async Task NativeFailureUsesSeparateStaticMessageWithoutExceptionText()
    {
        using var session = new LibVlcPlayerSession(_ => throw new InvalidOperationException("synthetic-secret"), (_, _) => Task.CompletedTask);
        await session.PlayAsync(Channel());
        Assert.Equal(PlayerState.Error, session.State);
        Assert.Contains("起播失败", session.ErrorMessage);
        Assert.False(session.ErrorMessage!.Contains("synthetic-secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PreflightFailureNeverStartsNativeOrLeaksExceptionText()
    {
        var starts = 0;
        using var session = new LibVlcPlayerSession(_ => { starts++; return true; }, (_, _) =>
            throw new InvalidOperationException("synthetic-secret http://origin.invalid/private"));
        await session.PlayAsync(Channel());
        Assert.Equal(0, starts);
        Assert.Equal(PlayerState.Error, session.State);
        Assert.Contains("代理", session.ErrorMessage);
        Assert.False(session.ErrorMessage!.Contains("synthetic-secret", StringComparison.Ordinal));
        Assert.False(session.ErrorMessage.Contains("origin.invalid", StringComparison.Ordinal));
    }
}
