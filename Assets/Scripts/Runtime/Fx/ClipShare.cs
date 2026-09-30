using System;
using System.IO;
using UnityEngine;

namespace PoDecath.Fx
{
    /// <summary>
    /// Hands a saved highlight clip to whatever the platform does with a file somebody wants to pass on.
    ///
    ///   Android   copies the GIF into Pictures/PoDecath through MediaStore (so it is in the gallery as well)
    ///             and opens the system share sheet on it. A file:// link cannot be shared on Android 7 and
    ///             later, and a FileProvider would need a manifest entry this project does not carry; the
    ///             MediaStore copy gives a content:// link with nothing added to the build.
    ///   Desktop   opens the clips folder, which is where anybody at a keyboard wants to be.
    ///   Elsewhere opens the file with the system, which on iOS is a no-op; the button says where it is.
    ///
    /// Everything is caught and reported as a line of text: a share that throws must not take the results
    /// card down with it.
    /// </summary>
    public static class ClipShare
    {
        /// <summary>Shares <paramref name="path"/>; returns one line saying what happened.</summary>
        public static string Share(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "No clip saved yet.";
            try
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                return ShareAndroid(path);
#elif UNITY_EDITOR || UNITY_STANDALONE
                Application.OpenURL(new Uri(Path.GetDirectoryName(path) + Path.DirectorySeparatorChar).AbsoluteUri);
                return $"Opened the clips folder: {Path.GetFileName(path)}";
#else
                Application.OpenURL(new Uri(path).AbsoluteUri);
                return $"Saved as {Path.GetFileName(path)}";
#endif
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ClipShare] {e.Message}");
                return $"Could not share; the clip is at {Path.GetFileName(path)}";
            }
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        static string ShareAndroid(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            var sbytes = new sbyte[bytes.Length];
            Buffer.BlockCopy(bytes, 0, sbytes, 0, bytes.Length);

            using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using AndroidJavaObject activity = player.GetStatic<AndroidJavaObject>("currentActivity");
            using AndroidJavaObject resolver = activity.Call<AndroidJavaObject>("getContentResolver");

            using var values = new AndroidJavaObject("android.content.ContentValues");
            values.Call("put", "_display_name", Path.GetFileName(path));
            values.Call("put", "mime_type", "image/gif");
            using var version = new AndroidJavaClass("android.os.Build$VERSION");
            if (version.GetStatic<int>("SDK_INT") >= 29) values.Call("put", "relative_path", "Pictures/PoDecath");

            using var media = new AndroidJavaClass("android.provider.MediaStore$Images$Media");
            using AndroidJavaObject collection = media.GetStatic<AndroidJavaObject>("EXTERNAL_CONTENT_URI");
            using AndroidJavaObject uri = resolver.Call<AndroidJavaObject>("insert", collection, values);
            if (uri == null) return "Could not add the clip to the gallery.";

            using (AndroidJavaObject stream = resolver.Call<AndroidJavaObject>("openOutputStream", uri))
            {
                stream.Call("write", sbytes);
                stream.Call("close");
            }

            using var intent = new AndroidJavaObject("android.content.Intent", "android.intent.action.SEND");
            intent.Call<AndroidJavaObject>("setType", "image/gif");
            intent.Call<AndroidJavaObject>("putExtra", "android.intent.extra.STREAM", uri);
            intent.Call<AndroidJavaObject>("addFlags", 1);   // FLAG_GRANT_READ_URI_PERMISSION
            using var intentClass = new AndroidJavaClass("android.content.Intent");
            using AndroidJavaObject chooser = intentClass.CallStatic<AndroidJavaObject>("createChooser", intent, "Share the highlight");
            activity.Call("startActivity", chooser);
            return "In your gallery under Pictures/PoDecath.";
        }
#endif
    }
}
