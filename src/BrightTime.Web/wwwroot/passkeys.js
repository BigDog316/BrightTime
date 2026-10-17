// Passkeys (WebAuthn) for BrightTime: progressive enhancement over server-rendered pages.
//
// WebAuthn is a browser API, so passkeys need this script; nothing else on the site does.
// Buttons marked .needs-webauthn are shown only where the browser supports passkeys, and
// are wired by event delegation, so they keep working across Blazor's enhanced navigation.
(() => {
    "use strict";

    const supported = typeof window.PublicKeyCredential === "function" && !!navigator.credentials;
    if (supported) {
        document.documentElement.classList.add("webauthn");
    }

    const fromBase64Url = (value) => {
        const base64 = value.replace(/-/g, "+").replace(/_/g, "/");
        const padded = base64 + "===".slice((base64.length + 3) % 4);
        return Uint8Array.from(atob(padded), (c) => c.charCodeAt(0)).buffer;
    };

    const toBase64Url = (buffer) => {
        let text = "";
        for (const byte of new Uint8Array(buffer)) {
            text += String.fromCharCode(byte);
        }
        return btoa(text).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
    };

    const antiforgeryToken = () =>
        document.querySelector('input[name="__RequestVerificationToken"]')?.value ?? "";

    async function post(url, body) {
        const response = await fetch(url, {
            method: "POST",
            credentials: "same-origin",
            headers: { "Content-Type": "application/json", "RequestVerificationToken": antiforgeryToken() },
            body: JSON.stringify(body ?? {}),
        });
        const data = await response.json().catch(() => ({}));
        if (response.status === 403 && data.confirm) {
            // Step-up: confirm it's you, then come back.
            location.assign(data.confirm);
            throw new Error("Confirm it's you to continue.");
        }
        if (!response.ok) {
            throw new Error(data.error ?? (response.status === 429
                ? "Too many attempts. Wait a minute and try again."
                : "Something went wrong. Try again."));
        }
        return data;
    }

    async function register() {
        const options = await post("/account/passkeys/options");
        options.challenge = fromBase64Url(options.challenge);
        options.user.id = fromBase64Url(options.user.id);
        options.excludeCredentials = (options.excludeCredentials ?? []).map((c) => ({ ...c, id: fromBase64Url(c.id) }));

        const credential = await navigator.credentials.create({ publicKey: options });
        await post("/account/passkeys", {
            name: document.getElementById("passkey-name")?.value ?? "",
            credential: {
                id: credential.id,
                rawId: toBase64Url(credential.rawId),
                type: credential.type,
                response: {
                    attestationObject: toBase64Url(credential.response.attestationObject),
                    clientDataJSON: toBase64Url(credential.response.clientDataJSON),
                    transports: credential.response.getTransports?.() ?? [],
                },
                clientExtensionResults: credential.getClientExtensionResults?.() ?? {},
            },
        });
        location.assign("/account/security?added=1");
    }

    async function signIn(button) {
        const options = await post("/sign-in/passkey/options");
        options.challenge = fromBase64Url(options.challenge);
        options.allowCredentials = (options.allowCredentials ?? []).map((c) => ({ ...c, id: fromBase64Url(c.id) }));

        const credential = await navigator.credentials.get({ publicKey: options });
        const result = await post("/sign-in/passkey", {
            returnUrl: button.dataset.returnUrl ?? null,
            credential: {
                id: credential.id,
                rawId: toBase64Url(credential.rawId),
                type: credential.type,
                response: {
                    authenticatorData: toBase64Url(credential.response.authenticatorData),
                    clientDataJSON: toBase64Url(credential.response.clientDataJSON),
                    signature: toBase64Url(credential.response.signature),
                    userHandle: credential.response.userHandle ? toBase64Url(credential.response.userHandle) : null,
                },
                clientExtensionResults: credential.getClientExtensionResults?.() ?? {},
            },
        });
        location.assign(result.redirect);
    }

    async function confirm(button) {
        const options = await post("/account/confirm/passkey/options");
        options.challenge = fromBase64Url(options.challenge);
        options.allowCredentials = (options.allowCredentials ?? []).map((c) => ({ ...c, id: fromBase64Url(c.id) }));

        const credential = await navigator.credentials.get({ publicKey: options });
        const result = await post("/account/confirm/passkey", {
            returnUrl: button.dataset.returnUrl ?? null,
            credential: {
                id: credential.id,
                rawId: toBase64Url(credential.rawId),
                type: credential.type,
                response: {
                    authenticatorData: toBase64Url(credential.response.authenticatorData),
                    clientDataJSON: toBase64Url(credential.response.clientDataJSON),
                    signature: toBase64Url(credential.response.signature),
                    userHandle: credential.response.userHandle ? toBase64Url(credential.response.userHandle) : null,
                },
                clientExtensionResults: credential.getClientExtensionResults?.() ?? {},
            },
        });
        location.assign(result.redirect);
    }

    function report(button, message) {
        const target = document.getElementById(button.dataset.errorTarget ?? "passkey-error");
        if (target) {
            target.textContent = message;
            target.hidden = false;
            target.focus?.();
        }
    }

    document.addEventListener("click", async (event) => {
        const button = event.target.closest?.("[data-passkey-action]");
        if (!button || !supported) {
            return;
        }

        event.preventDefault();
        button.disabled = true;
        try {
            if (button.dataset.passkeyAction === "register") {
                await register();
            } else if (button.dataset.passkeyAction === "confirm") {
                await confirm(button);
            } else {
                await signIn(button);
            }
        } catch (error) {
            // NotAllowedError covers both "cancelled" and "timed out": the browser will not
            // say which, deliberately.
            report(button, error?.name === "NotAllowedError"
                ? "The passkey request was cancelled or timed out."
                : error?.name === "InvalidStateError"
                    ? "This device already has a passkey for your account."
                    : error?.message ?? "Something went wrong. Try again.");
        } finally {
            button.disabled = false;
        }
    });
})();
