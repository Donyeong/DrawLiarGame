package com.rascallab.drawliar.accounts;

import android.content.Context;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.AtomicFile;
import android.util.Base64;
import androidx.annotation.Keep;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.nio.charset.StandardCharsets;
import java.security.KeyStore;
import java.util.Arrays;
import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;

@Keep
public final class DrawGuestCredentials {
    private static final String KEY_ALIAS = "com.rascallab.drawliar.guest.v1";
    private static final String FILE_NAME = "drawliar_guest_v1.bin";
    private static final int MAX_LENGTH = 16384;
    private static final int BASE64_FLAGS = Base64.NO_WRAP;

    private DrawGuestCredentials() { }

    public static synchronized String Load(Context context) throws Exception {
        AtomicFile file = Store(context);
        if (!file.getBaseFile().exists() && !new File(file.getBaseFile().getPath() + ".bak").exists()) return null;
        byte[] encrypted = null;
        byte[] plaintext = null;
        try (FileInputStream input = file.openRead()) {
            long length = input.getChannel().size();
            if (length <= 0 || length > MAX_LENGTH) throw new IllegalStateException("guest_storage_invalid");
            encrypted = new byte[(int)length];
            int offset = 0;
            while (offset < encrypted.length) {
                int read = input.read(encrypted, offset, encrypted.length - offset);
                if (read <= 0) throw new IllegalStateException("guest_storage_invalid");
                offset += read;
            }
            String[] parts = new String(encrypted, StandardCharsets.US_ASCII).split("\\.", -1);
            if (parts.length != 2) throw new IllegalStateException("guest_storage_invalid");
            byte[] iv = Base64.decode(parts[0], BASE64_FLAGS);
            if (iv.length != 12) throw new IllegalStateException("guest_storage_invalid");
            Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
            cipher.init(Cipher.DECRYPT_MODE, Key(false), new GCMParameterSpec(128, iv));
            cipher.updateAAD(Binding(context));
            plaintext = cipher.doFinal(Base64.decode(parts[1], BASE64_FLAGS));
            if (plaintext.length <= 0 || plaintext.length > 4096) throw new IllegalStateException("guest_storage_invalid");
            return new String(plaintext, StandardCharsets.UTF_8);
        } finally { Clear(encrypted); Clear(plaintext); }
    }

    public static synchronized String SaveIfMissing(Context context, String payload) throws Exception {
        String existing = Load(context);
        return existing != null ? existing : Save(context, payload);
    }

    public static synchronized String Save(Context context, String payload) throws Exception {
        if (payload == null || payload.isEmpty() || payload.length() > 4096) throw new IllegalStateException("guest_storage_invalid");
        byte[] plaintext = payload.getBytes(StandardCharsets.UTF_8);
        byte[] encrypted = null;
        AtomicFile file = Store(context);
        FileOutputStream output = null;
        try {
            Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
            cipher.init(Cipher.ENCRYPT_MODE, Key(true));
            cipher.updateAAD(Binding(context));
            encrypted = (Base64.encodeToString(cipher.getIV(), BASE64_FLAGS) + "." +
                Base64.encodeToString(cipher.doFinal(plaintext), BASE64_FLAGS)).getBytes(StandardCharsets.US_ASCII);
            output = file.startWrite();
            output.write(encrypted);
            file.finishWrite(output);
            output = null;
            return payload;
        } finally {
            if (output != null) file.failWrite(output);
            Clear(plaintext); Clear(encrypted);
        }
    }

    public static synchronized void Delete(Context context) throws Exception {
        Store(context).delete();
        KeyStore keyStore = KeyStore.getInstance("AndroidKeyStore");
        keyStore.load(null);
        if (keyStore.containsAlias(KEY_ALIAS)) keyStore.deleteEntry(KEY_ALIAS);
    }

    private static AtomicFile Store(Context context) {
        return new AtomicFile(new File(context.getApplicationContext().getNoBackupFilesDir(), FILE_NAME));
    }

    private static byte[] Binding(Context context) {
        return (context.getApplicationContext().getPackageName() + ":" + KEY_ALIAS).getBytes(StandardCharsets.UTF_8);
    }

    private static SecretKey Key(boolean create) throws Exception {
        KeyStore keyStore = KeyStore.getInstance("AndroidKeyStore");
        keyStore.load(null);
        if (!keyStore.containsAlias(KEY_ALIAS)) {
            if (!create) throw new IllegalStateException("guest_storage_key_unavailable");
            KeyGenerator generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore");
            generator.init(new KeyGenParameterSpec.Builder(KEY_ALIAS, KeyProperties.PURPOSE_ENCRYPT | KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE).build());
            generator.generateKey();
        }
        return (SecretKey)keyStore.getKey(KEY_ALIAS, null);
    }

    private static void Clear(byte[] bytes) { if (bytes != null) Arrays.fill(bytes, (byte)0); }
}
