package com.rascallab.drawliar.media;

import android.content.ContentResolver;
import android.content.ContentValues;
import android.content.Context;
import android.net.Uri;
import android.os.Build;
import android.os.Environment;
import android.provider.MediaStore;
import androidx.annotation.Keep;
import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.OutputStream;

@Keep
public final class DrawDrawingImage {
    private DrawDrawingImage() { }

    public static String Save(Context context, byte[] png, String fileName) throws IOException {
        if (png == null || png.length == 0 || fileName == null || !fileName.matches("[\\p{L}\\p{N}_-]+\\.png"))
            throw new IOException("invalid_image");
        return Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q
            ? SaveGallery(context, png, fileName) : SavePrivate(context, png, fileName);
    }

    private static String SaveGallery(Context context, byte[] png, String fileName) throws IOException {
        ContentResolver resolver = context.getContentResolver();
        ContentValues values = new ContentValues();
        values.put(MediaStore.Images.Media.DISPLAY_NAME, fileName);
        values.put(MediaStore.Images.Media.MIME_TYPE, "image/png");
        values.put(MediaStore.Images.Media.RELATIVE_PATH, Environment.DIRECTORY_PICTURES + "/DrawLiar");
        values.put(MediaStore.Images.Media.IS_PENDING, 1);
        Uri image = resolver.insert(MediaStore.Images.Media.EXTERNAL_CONTENT_URI, values);
        if (image == null) throw new IOException("image_unavailable");
        boolean saved = false;
        try {
            try (OutputStream output = resolver.openOutputStream(image, "w")) {
                if (output == null) throw new IOException("image_unavailable");
                output.write(png);
                output.flush();
            }
            ContentValues completed = new ContentValues();
            completed.put(MediaStore.Images.Media.IS_PENDING, 0);
            if (resolver.update(image, completed, null, null) != 1) throw new IOException("image_unavailable");
            saved = true;
            return image.toString();
        } finally {
            if (!saved) resolver.delete(image, null, null);
        }
    }

    private static String SavePrivate(Context context, byte[] png, String fileName) throws IOException {
        File pictures = context.getExternalFilesDir(Environment.DIRECTORY_PICTURES);
        if (pictures == null) pictures = new File(context.getFilesDir(), Environment.DIRECTORY_PICTURES);
        File directory = new File(pictures, "DrawLiar");
        if (!directory.isDirectory() && !directory.mkdirs()) throw new IOException("image_unavailable");
        File image = new File(directory, fileName);
        if (!image.createNewFile()) throw new IOException("image_already_exists");
        boolean saved = false;
        try {
            try (FileOutputStream output = new FileOutputStream(image)) {
                output.write(png);
                output.flush();
            }
            saved = true;
            return image.getAbsolutePath();
        } finally {
            if (!saved) image.delete();
        }
    }
}
