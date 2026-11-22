using Koth.Contracts;
using Microsoft.JSInterop;

namespace Koth.Browser;

internal static class PanelOperations
{
    internal static ValueTask<T> CallAsync<T>(this IJSRuntime js, string operation, object payload, CancellationToken token) =>
        RequestAsync<T>(js, KothProtocol.PluginId, operation, payload, token);

    internal static ValueTask<bool> CanAsync(this IJSRuntime js, string policy, CancellationToken token) =>
        RequestAsync<bool>(js, "quasar.core", "dashboard.permission", new { policy }, token);

    private static async ValueTask<T> RequestAsync<T>(IJSRuntime js, string plugin, string operation, object payload, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // The SDK reads options.signal. Keep the controller until its request finishes.
        await using var controller = await js.InvokeConstructorAsync("AbortController");
        try
        {
            return await js.InvokeAsync<T>("quasarSdk.request", token, plugin, operation, payload, controller);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await controller.InvokeVoidAsync("abort");
            throw;
        }
    }
}
