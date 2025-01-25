using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using UnityEngine;

namespace SusAccess.Speech
{
    // Handles text-to-speech functionality using the Tolk library
    public static class SpeechSynthesizer
    {
        // DLL file names for required libraries
        private const string TolkDllName = "Tolk.dll";
        private const string TolkDotNetDllName = "TolkDotNet.dll";
        private const string NvdaClientDllName = "nvdaControllerClient32.dll";
        private const string SapiDllName = "SAAPI32.dll";

        private static Dictionary<string, IntPtr> loadedDlls = new Dictionary<string, IntPtr>();
        private static string tempDirectory;
        private static ManualLogSource logger;

        // Native methods for DLL handling
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [DllImport("kernel32.dll")]
        private static extern bool FreeLibrary(IntPtr hModule);

        // Function delegate types for Tolk methods
        private delegate void LoadDelegate();
        private delegate void UnloadDelegate();
        private delegate bool OutputDelegate([MarshalAs(UnmanagedType.LPWStr)] string str, bool interrupt);
        private delegate IntPtr DetectScreenReaderDelegate();

        // Tolk function pointers
        private static LoadDelegate Tolk_Load;
        private static UnloadDelegate Tolk_Unload;
        private static OutputDelegate Tolk_Output;
        private static DetectScreenReaderDelegate Tolk_DetectScreenReader;

        // Initializes the speech synthesizer and loads required DLLs
        public static void Initialize(ManualLogSource logSource)
        {
            logger = logSource;
            try
            {
                // Set up DLL directory
                string assemblyLocation = Assembly.GetExecutingAssembly().Location;
                string pluginPath = Path.GetDirectoryName(assemblyLocation);
                tempDirectory = Path.Combine(pluginPath, "SpeechLibs");
                Directory.CreateDirectory(tempDirectory);

                // Extract and load all required DLLs
                ExtractAndLoadDll(TolkDllName);
                ExtractAndLoadDll(TolkDotNetDllName);
                ExtractAndLoadDll(NvdaClientDllName);
                ExtractAndLoadDll(SapiDllName);

                // Initialize Tolk if main DLL loaded successfully
                if (loadedDlls.ContainsKey(TolkDllName))
                {
                    InitializeTolk(loadedDlls[TolkDllName]);
                }
                else
                {
                    logger.LogError("Failed to initialize Tolk. Speech synthesis will not be available.");
                }
            }
            catch (Exception e)
            {
                logger.LogError($"Error initializing speech synthesizer: {e.Message}");
                logger.LogError($"Stack trace: {e.StackTrace}");
            }
        }

        // Extracts embedded DLL resources and loads them
        private static void ExtractAndLoadDll(string dllName)
        {
            string resourceName = $"SusAccess.{dllName}";

            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                {
                    logger.LogError($"Failed to find embedded resource: {resourceName}");
                    return;
                }

                // Extract DLL to temp directory
                string tempFilePath = Path.Combine(tempDirectory, dllName);
                using (FileStream fileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write))
                {
                    stream.CopyTo(fileStream);
                }

                // Load the extracted DLL
                IntPtr dllHandle = LoadLibrary(tempFilePath);
                if (dllHandle != IntPtr.Zero)
                {
                    loadedDlls[dllName] = dllHandle;
                }
                else
                {
                    int errorCode = Marshal.GetLastWin32Error();
                    logger.LogWarning($"Failed to load {dllName}. Error code: {errorCode}");
                }
            }
        }

        // Gets a function pointer from a DLL and converts it to a delegate
        private static T GetDelegate<T>(IntPtr module, string procName) where T : class
        {
            IntPtr pAddressOfFunctionToCall = GetProcAddress(module, procName);
            if (pAddressOfFunctionToCall == IntPtr.Zero)
            {
                throw new Exception($"Failed to get address of {procName}");
            }
            return Marshal.GetDelegateForFunctionPointer(pAddressOfFunctionToCall, typeof(T)) as T;
        }

        // Sets up Tolk function pointers and initializes the library
        private static void InitializeTolk(IntPtr tolkDllHandle)
        {
            // Get function pointers
            Tolk_Load = GetDelegate<LoadDelegate>(tolkDllHandle, "Tolk_Load");
            Tolk_Unload = GetDelegate<UnloadDelegate>(tolkDllHandle, "Tolk_Unload");
            Tolk_Output = GetDelegate<OutputDelegate>(tolkDllHandle, "Tolk_Output");
            Tolk_DetectScreenReader = GetDelegate<DetectScreenReaderDelegate>(tolkDllHandle, "Tolk_DetectScreenReader");

            // Initialize Tolk
            Tolk_Load();

            // Log detected screen reader
            IntPtr pScreenReader = Tolk_DetectScreenReader();
            string detectedReader = pScreenReader != IntPtr.Zero ? Marshal.PtrToStringUni(pScreenReader) : "None";
            logger.LogInfo($"Tolk initialized. Detected screen reader: {detectedReader}");
        }

        // Speaks text using the initialized screen reader
        public static void SpeakText(string text, bool interrupt = false)
        {
            if (string.IsNullOrEmpty(text)) return;

            if (Tolk_Output != null)
            {
                bool result = Tolk_Output(text, interrupt);
                logger.LogInfo($"Speech output: {text} (success: {result})");
            }
        }

        // Cleans up resources and unloads DLLs
        public static void Shutdown()
        {
            if (Tolk_Unload != null)
            {
                Tolk_Unload();
                logger.LogInfo("Tolk unloaded.");
            }

            foreach (var dll in loadedDlls.Values)
            {
                FreeLibrary(dll);
            }
            loadedDlls.Clear();
        }
    }
}