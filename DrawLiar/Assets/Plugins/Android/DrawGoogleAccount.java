package com.drawliar.accounts;

import android.app.Activity;
import android.os.CancellationSignal;
import androidx.annotation.Keep;
import androidx.credentials.Credential;
import androidx.credentials.CredentialManager;
import androidx.credentials.CredentialManagerCallback;
import androidx.credentials.CustomCredential;
import androidx.credentials.GetCredentialRequest;
import androidx.credentials.GetCredentialResponse;
import androidx.credentials.exceptions.GetCredentialCancellationException;
import androidx.credentials.exceptions.GetCredentialException;
import com.google.android.libraries.identity.googleid.GetSignInWithGoogleOption;
import com.google.android.libraries.identity.googleid.GoogleIdTokenCredential;
import java.util.concurrent.ConcurrentHashMap;

@Keep
public final class DrawGoogleAccount {
    private static final ConcurrentHashMap<String, CancellationSignal> REQUESTS = new ConcurrentHashMap<>();

    @Keep
    public interface Callback { void OnCompleted(String idToken, String error); }

    public static void SignIn(Activity activity, String requestId, String webClientId, String nonce, Callback callback) {
        CancellationSignal signal = new CancellationSignal();
        REQUESTS.put(requestId, signal);
        activity.runOnUiThread(() -> {
            if (signal.isCanceled()) return;
            try {
                GetSignInWithGoogleOption option = new GetSignInWithGoogleOption.Builder(webClientId).setNonce(nonce).build();
                GetCredentialRequest request = new GetCredentialRequest.Builder().addCredentialOption(option).build();
                CredentialManager.create(activity).getCredentialAsync(activity, request, signal, activity::runOnUiThread,
                    new CredentialManagerCallback<GetCredentialResponse, GetCredentialException>() {
                        @Override
                        public void onResult(GetCredentialResponse response) {
                            if (REQUESTS.remove(requestId) == null) return;
                            try {
                                Credential credential = response.getCredential();
                                if (!(credential instanceof CustomCredential) ||
                                    !GoogleIdTokenCredential.TYPE_GOOGLE_ID_TOKEN_CREDENTIAL.equals(credential.getType())) {
                                    callback.OnCompleted("", "invalid_credential");
                                    return;
                                }
                                callback.OnCompleted(GoogleIdTokenCredential.createFrom(credential.getData()).getIdToken(), "");
                            } catch (Exception ignored) { callback.OnCompleted("", "invalid_credential"); }
                        }

                        @Override
                        public void onError(GetCredentialException exception) {
                            if (REQUESTS.remove(requestId) != null)
                                callback.OnCompleted("", exception instanceof GetCredentialCancellationException ? "cancelled" : "unavailable");
                        }
                    });
            } catch (Exception ignored) {
                if (REQUESTS.remove(requestId) != null) callback.OnCompleted("", "unavailable");
            }
        });
    }

    public static void Cancel(String requestId) {
        CancellationSignal signal = REQUESTS.remove(requestId);
        if (signal != null) signal.cancel();
    }
}
