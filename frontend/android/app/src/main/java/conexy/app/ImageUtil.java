package conexy.app;

import android.content.Context;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Matrix;
import android.net.Uri;
import android.util.Base64;

import androidx.exifinterface.media.ExifInterface;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;

/** Декодирование с уменьшением, поворот по EXIF, сжатие в JPEG. */
final class ImageUtil {
    private ImageUtil() {}

    static Bitmap decode(Context context, Uri uri, int maxSide) throws IOException {
        BitmapFactory.Options bounds = new BitmapFactory.Options();
        bounds.inJustDecodeBounds = true;
        try (InputStream in = UriResolver.open(context, uri)) {
            BitmapFactory.decodeStream(in, null, bounds);
        }
        if (bounds.outWidth <= 0 || bounds.outHeight <= 0) {
            throw new IOException("Файл не является изображением");
        }
        int sample = 1;
        int longest = Math.max(bounds.outWidth, bounds.outHeight);
        while (longest / (sample * 2) >= maxSide) {
            sample *= 2;
        }
        BitmapFactory.Options options = new BitmapFactory.Options();
        options.inSampleSize = sample;
        Bitmap bitmap;
        try (InputStream in = UriResolver.open(context, uri)) {
            bitmap = BitmapFactory.decodeStream(in, null, options);
        }
        if (bitmap == null) {
            throw new IOException("Не удалось декодировать изображение");
        }
        int orientation = ExifInterface.ORIENTATION_NORMAL;
        try (InputStream in = UriResolver.open(context, uri)) {
            orientation = new ExifInterface(in).getAttributeInt(
                    ExifInterface.TAG_ORIENTATION, ExifInterface.ORIENTATION_NORMAL);
        } catch (Exception ignored) {
            // нет EXIF — оставляем как есть
        }
        Matrix matrix = new Matrix();
        switch (orientation) {
            case ExifInterface.ORIENTATION_ROTATE_90:
                matrix.postRotate(90);
                break;
            case ExifInterface.ORIENTATION_ROTATE_180:
                matrix.postRotate(180);
                break;
            case ExifInterface.ORIENTATION_ROTATE_270:
                matrix.postRotate(270);
                break;
            case ExifInterface.ORIENTATION_FLIP_HORIZONTAL:
                matrix.postScale(-1, 1);
                break;
            case ExifInterface.ORIENTATION_FLIP_VERTICAL:
                matrix.postScale(1, -1);
                break;
            default:
                return scaleDown(bitmap, maxSide);
        }
        Bitmap rotated = Bitmap.createBitmap(bitmap, 0, 0, bitmap.getWidth(), bitmap.getHeight(), matrix, true);
        if (rotated != bitmap) {
            bitmap.recycle();
        }
        return scaleDown(rotated, maxSide);
    }

    static Bitmap scaleDown(Bitmap src, int maxSide) {
        int longest = Math.max(src.getWidth(), src.getHeight());
        if (longest <= maxSide) {
            return src;
        }
        float ratio = (float) maxSide / longest;
        int width = Math.max(1, Math.round(src.getWidth() * ratio));
        int height = Math.max(1, Math.round(src.getHeight() * ratio));
        Bitmap scaled = Bitmap.createScaledBitmap(src, width, height, true);
        if (scaled != src) {
            src.recycle();
        }
        return scaled;
    }

    /** Уменьшает до maxSide и кодирует в JPEG (прозрачность заливается белым). */
    static byte[] toJpeg(Bitmap src, int maxSide, int quality) {
        Bitmap bitmap = scaleDown(src, maxSide);
        if (bitmap.hasAlpha()) {
            Bitmap flat = Bitmap.createBitmap(bitmap.getWidth(), bitmap.getHeight(), Bitmap.Config.ARGB_8888);
            Canvas canvas = new Canvas(flat);
            canvas.drawColor(Color.WHITE);
            canvas.drawBitmap(bitmap, 0, 0, null);
            bitmap.recycle();
            bitmap = flat;
        }
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        bitmap.compress(Bitmap.CompressFormat.JPEG, quality, out);
        bitmap.recycle();
        return out.toByteArray();
    }

    static String base64(byte[] data) {
        return Base64.encodeToString(data, Base64.NO_WRAP);
    }
}
