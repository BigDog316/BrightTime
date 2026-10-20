using Android.OS;
using AndroidX.Core.Content;
using AndroidX.Credentials;
using AndroidX.Credentials.Exceptions;
using BrightTime.Ui.Shared.Platform;

namespace BrightTime.Mobile;

/// <summary>
/// Passkeys through Android's Credential Manager (D38): Google Password Manager, or whichever
/// provider the person chose, holds the key. The site's <c>/.well-known/assetlinks.json</c> must
/// name this app (<c>get_login_creds</c>, D35) for Android to let it use the region's passkeys.
/// </summary>
/// <remarks>
/// Credential Manager takes the server's WebAuthn options as JSON and answers in the same JSON,
/// so nothing here reads either: the server checks the answer (D38).
/// </remarks>
internal sealed class AndroidPasskeys : IPasskeyAuthenticator
{
    // Credential Manager's passkeys need Android 9 (API 28); Play services carries them below 14.
    public bool IsSupported => OperatingSystem.IsAndroidVersionAtLeast(28);

    public Task<PasskeyOutcome> CreateAsync(string optionsJson, CancellationToken cancellationToken = default) =>
        RunAsync((manager, activity, signal, callback) =>
            manager.CreateCredentialAsync(activity, new CreatePublicKeyCredentialRequest(optionsJson), signal, ContextCompat.GetMainExecutor(activity)!, callback),
            result => (result as CreatePublicKeyCredentialResponse)?.RegistrationResponseJson,
            cancellationToken);

    public Task<PasskeyOutcome> GetAsync(string optionsJson, CancellationToken cancellationToken = default) =>
        RunAsync((manager, activity, signal, callback) =>
            manager.GetCredentialAsync(
                activity,
                new GetCredentialRequest.Builder().AddCredentialOption(new GetPublicKeyCredentialOption(optionsJson)).Build(),
                signal,
                ContextCompat.GetMainExecutor(activity)!,
                callback),
            result => ((result as GetCredentialResponse)?.Credential as PublicKeyCredential)?.AuthenticationResponseJson,
            cancellationToken);

    private static async Task<PasskeyOutcome> RunAsync(
        Action<ICredentialManager, Android.App.Activity, CancellationSignal, ICredentialManagerCallback> start,
        Func<Java.Lang.Object?, string?> read,
        CancellationToken cancellationToken)
    {
        if (Platform.CurrentActivity is not { } activity)
        {
            return PasskeyOutcome.Failed;
        }

        using CancellationSignal signal = new();
        using CancellationTokenRegistration cancel = cancellationToken.Register(signal.Cancel);
        using Callback callback = new(read);
        await MainThread.InvokeOnMainThreadAsync(() => start(CredentialManager.Create(activity), activity, signal, callback)).ConfigureAwait(false);
        return await callback.Outcome.ConfigureAwait(false);
    }

    private sealed class Callback(Func<Java.Lang.Object?, string?> read) : Java.Lang.Object, ICredentialManagerCallback
    {
        private readonly TaskCompletionSource<PasskeyOutcome> _outcome = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<PasskeyOutcome> Outcome => _outcome.Task;

        public void OnResult(Java.Lang.Object? result) =>
            _outcome.TrySetResult(read(result) is { } json ? new PasskeyOutcome(json) : PasskeyOutcome.Failed);

        // The error is a Java throwable, which the binding hands over as a plain object: told apart by its class.
        public void OnError(Java.Lang.Object e) =>
            _outcome.TrySetResult(e.Class.SimpleName is nameof(CreateCredentialCancellationException) or nameof(GetCredentialCancellationException)
                ? PasskeyOutcome.Declined
                : PasskeyOutcome.Failed);
    }
}
