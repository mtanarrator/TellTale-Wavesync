using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using VegasAudioSync.Models;

namespace VegasAudioSync.Interop
{
    public class NativeSyncEngine
    {
        private const string DllName = "telltale_sync_core.dll";

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int NativeProgressCallback(float progress, IntPtr messagePtr, IntPtr userData);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string libname);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr vegas_sync_version();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void vegas_sync_free_string(IntPtr ptr);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int vegas_sync_execute(
            IntPtr jsonRequestUtf8,
            [MarshalAs(UnmanagedType.FunctionPtr)] NativeProgressCallback progressCallback,
            IntPtr userData,
            out IntPtr outJsonManifestUtf8);

        private static bool _libraryPreloaded = false;
        private static readonly object _preloadLock = new object();

        public static void EnsureLibraryLoaded()
        {
            if (_libraryPreloaded) return;

            lock (_preloadLock)
            {
                if (_libraryPreloaded) return;

                // Determine base directory of this assembly
                string assemblyPath = Assembly.GetExecutingAssembly().Location;
                string assemblyDir = Path.GetDirectoryName(assemblyPath);

                if (!string.IsNullOrEmpty(assemblyDir))
                {
                    string targetDllPath = Path.Combine(assemblyDir, DllName);
                    if (File.Exists(targetDllPath))
                    {
                        LoadLibrary(targetDllPath);
                    }
                }

                _libraryPreloaded = true;
            }
        }

        public string GetEngineVersion()
        {
            EnsureLibraryLoaded();

            IntPtr versionPtr = vegas_sync_version();
            if (versionPtr == IntPtr.Zero)
            {
                return "Unknown (Failed to query native version)";
            }

            try
            {
                return Utf8PtrToString(versionPtr);
            }
            finally
            {
                vegas_sync_free_string(versionPtr);
            }
        }

        public SyncManifest ExecuteSync(
            SyncTaskRequest request,
            Action<float, string> onProgress = null,
            Func<bool> shouldCancel = null)
        {
            EnsureLibraryLoaded();

            if (request == null)
                throw new ArgumentNullException(nameof(request));

            string json = request.ToJson();
            byte[] utf8Bytes = Encoding.UTF8.GetBytes(json + "\0");

            GCHandle pinnedBytes = GCHandle.Alloc(utf8Bytes, GCHandleType.Pinned);
            IntPtr manifestPtr = IntPtr.Zero;

            try
            {
                NativeProgressCallback callback = null;
                if (onProgress != null || shouldCancel != null)
                {
                    callback = (progress, msgPtr, userData) =>
                    {
                        // Non-zero return asks the native engine to stop at the next checkpoint.
                        if (shouldCancel != null && shouldCancel())
                        {
                            return 1;
                        }

                        if (onProgress != null)
                        {
                            string message = Utf8PtrToString(msgPtr);
                            onProgress(progress, message);
                        }

                        return 0;
                    };
                }

                int statusCode = vegas_sync_execute(
                    pinnedBytes.AddrOfPinnedObject(),
                    callback,
                    IntPtr.Zero,
                    out manifestPtr);

                // The native engine holds the function pointer for the whole call;
                // make sure the delegate is not collected mid-flight.
                GC.KeepAlive(callback);

                if (statusCode == NativeErrorCancelled)
                {
                    throw new OperationCanceledException("Synchronization was cancelled.");
                }

                if (statusCode != 0)
                {
                    throw new InvalidOperationException($"Native sync engine returned error code: {statusCode}");
                }

                if (manifestPtr == IntPtr.Zero)
                {
                    throw new InvalidOperationException("Native sync engine returned a null manifest pointer.");
                }

                string manifestJson = Utf8PtrToString(manifestPtr);
                return SyncManifest.FromJson(manifestJson);
            }
            finally
            {
                if (pinnedBytes.IsAllocated)
                {
                    pinnedBytes.Free();
                }

                if (manifestPtr != IntPtr.Zero)
                {
                    vegas_sync_free_string(manifestPtr);
                }
            }
        }

        private const int NativeErrorCancelled = -5;

        private static string Utf8PtrToString(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero) return null;

            int len = 0;
            while (Marshal.ReadByte(ptr, len) != 0)
            {
                len++;
            }

            byte[] buffer = new byte[len];
            Marshal.Copy(ptr, buffer, 0, len);
            return Encoding.UTF8.GetString(buffer);
        }
    }
}
