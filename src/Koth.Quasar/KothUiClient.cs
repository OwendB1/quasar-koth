using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Koth.Contracts;
using Quasar.Plugin.Abstractions.Operations;

namespace Koth.Quasar;

// Page settings and server panels use the host's current user and dispatcher.
internal sealed class KothUiClient(IQuasarOperationDispatcher dispatcher,
    AuthenticationStateProvider authentication, IServiceProvider services)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal async Task<T> CallAsync<T>(string operation, object request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        token.ThrowIfCancellationRequested();
        var result = await dispatcher.DispatchAsync(new(user, services), KothProtocol.PluginId, operation,
            JsonSerializer.SerializeToElement(request, Json), token);
        if (result.StatusCode != 200) throw new InvalidOperationException(result.Error ?? "KOTH operation was refused.");
        token.ThrowIfCancellationRequested();
        return (T)result.Value!;
    }

    internal async Task<bool> CanAsync(string policy)
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        return (await services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(user, policy)).Succeeded;
    }
}
