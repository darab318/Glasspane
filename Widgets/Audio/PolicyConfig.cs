using System;
using System.Runtime.InteropServices;

namespace Glasspane.Widgets.Audio
{
    /// <summary>
    /// Changing the default output device has no public Windows API. This undocumented
    /// interface is what the Sound settings page itself uses, and what EarTrumpet and
    /// SoundSwitch rely on. It has been stable since Windows 7.
    /// </summary>
    internal static class PolicyConfig
    {
        [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPolicyConfig
        {
            [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr format);
            [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int isDefault, IntPtr format);
            [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
            [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpointFormat, IntPtr mixFormat);
            [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int isDefault, IntPtr defaultPeriod, IntPtr minimumPeriod);
            [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr period);
            [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
            [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
            [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int fxStore, IntPtr key, IntPtr value);
            [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int fxStore, IntPtr key, IntPtr value);
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
            [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
        }

        [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
        private class PolicyConfigClient
        {
        }

        /// <summary>Makes a device the default for everything: games/media, system sounds and calls.</summary>
        public static bool SetDefaultDevice(string deviceId)
        {
            object? client = null;
            try
            {
                client = new PolicyConfigClient();
                var policy = (IPolicyConfig)client;
                bool ok = true;
                for (int role = 0; role <= 2; role++) // console, multimedia, communications
                    ok &= policy.SetDefaultEndpoint(deviceId, role) == 0;
                return ok;
            }
            catch (Exception ex)
            {
                Core.Log.Write("Could not switch audio device: " + ex.Message);
                return false;
            }
            finally
            {
                if (client != null) Marshal.ReleaseComObject(client);
            }
        }
    }
}
