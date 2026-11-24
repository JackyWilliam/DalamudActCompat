using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace DalamudActCompat.Host;

internal sealed unsafe class WindowsNotificationCenter
{
    private const string AppUserModelId = "DalamudActCompat.Host";
    private const string AppDisplayName = "DACT";
    private static readonly object RegistrationLock = new();
    private static bool registered;

    // Standard Windows 10 WinRT ABI IIDs (stable since 10.0.10240).
    private static readonly Guid IidXmlDocumentIo = new("6CD0E74E-EE65-4489-9EBF-CA43E87BA637");
    private static readonly Guid IidToastNotificationFactory = new("04124B20-82C6-4229-B109-FD9ED4662B53");
    private static readonly Guid IidToastNotificationManagerStatics = new("50AC103F-D235-4598-BBEF-98FE4D1A3AD4");
    private static readonly Guid IidPropertyValueStatics = new("629BDBC8-D932-4FF4-96B9-8D96C5C1E858");

    public bool TryShow(string title, string message, string? detail = null)
    {
        try
        {
            EnsureRegistered();
            var builder = new ToastContentBuilder()
                .AddText(title)
                .AddText(message);
            if (!string.IsNullOrWhiteSpace(detail))
            {
                builder.AddText(detail);
            }

            ShowToastXml(builder.GetXml(), DateTimeOffset.UtcNow.AddHours(12));
            return true;
        }
        catch (Exception ex)
        {
            HostPluginBridge.ReportException("windows-notification", "Notification Center", ex);
            return false;
        }
    }

    private static void EnsureRegistered()
    {
        lock (RegistrationLock)
        {
            if (registered)
            {
                return;
            }

            // Unpackaged desktop apps register an HKCU AppUserModelId so Windows Action Center
            // displays a stable identity without requiring Microsoft.Windows.SDK.NET or WinAppSDK.
            using var key = Registry.CurrentUser.CreateSubKey(
                $@"Software\Classes\AppUserModelId\{AppUserModelId}");
            key?.SetValue("DisplayName", AppDisplayName, RegistryValueKind.String);
            _ = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
            registered = true;
        }
    }

    private static void ShowToastXml(string xml, DateTimeOffset expirationUtc)
    {
        nint xmlDoc = 0;
        nint xmlDocIo = 0;
        nint toastFactory = 0;
        nint toast = 0;
        nint propFactory = 0;
        nint expirationProp = 0;
        nint managerStatics = 0;
        nint notifier = 0;
        nint hXmlClass = 0;
        nint hXmlText = 0;
        nint hToastClass = 0;
        nint hPropClass = 0;
        nint hManagerClass = 0;
        nint hAppId = 0;

        try
        {
            hXmlClass = CreateHString("Windows.Data.Xml.Dom.XmlDocument");
            CheckHr(RoActivateInstance(hXmlClass, &xmlDoc), "RoActivateInstance(XmlDocument)");

            var iidXmlIo = IidXmlDocumentIo;
            CheckHr(QueryInterface(xmlDoc, &iidXmlIo, &xmlDocIo), "QI(IXmlDocumentIO)");

            hXmlText = CreateHString(xml);
            // IXmlDocumentIO.LoadXml is vtable slot 6 (after 6 IInspectable slots).
            var loadXml = (delegate* unmanaged[Stdcall]<nint, nint, int>)GetVTableSlot(xmlDocIo, 6);
            CheckHr(loadXml(xmlDocIo, hXmlText), "IXmlDocumentIO.LoadXml");

            hToastClass = CreateHString("Windows.UI.Notifications.ToastNotification");
            var iidToastFactory = IidToastNotificationFactory;
            CheckHr(
                RoGetActivationFactory(hToastClass, &iidToastFactory, &toastFactory),
                "RoGetActivationFactory(ToastNotification)");

            // IToastNotificationFactory.CreateToastNotification is slot 6.
            var createToast = (delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)GetVTableSlot(toastFactory, 6);
            CheckHr(createToast(toastFactory, xmlDoc, &toast), "CreateToastNotification");

            // Set 12-hour expiration via IPropertyValueStatics.CreateDateTime (slot 21) -> IToastNotification.put_ExpirationTime (slot 7).
            hPropClass = CreateHString("Windows.Foundation.PropertyValue");
            var iidPropStatics = IidPropertyValueStatics;
            if (RoGetActivationFactory(hPropClass, &iidPropStatics, &propFactory) >= 0 && propFactory != 0)
            {
                var createDateTime = (delegate* unmanaged[Stdcall]<nint, long, nint*, int>)GetVTableSlot(propFactory, 21);
                if (createDateTime(propFactory, expirationUtc.ToFileTime(), &expirationProp) >= 0 && expirationProp != 0)
                {
                    var putExpiration = (delegate* unmanaged[Stdcall]<nint, nint, int>)GetVTableSlot(toast, 7);
                    _ = putExpiration(toast, expirationProp);
                }
            }

            hManagerClass = CreateHString("Windows.UI.Notifications.ToastNotificationManager");
            var iidManager = IidToastNotificationManagerStatics;
            CheckHr(
                RoGetActivationFactory(hManagerClass, &iidManager, &managerStatics),
                "RoGetActivationFactory(ToastNotificationManager)");

            hAppId = CreateHString(AppUserModelId);
            // IToastNotificationManagerStatics.CreateToastNotifierWithId is slot 7.
            var createNotifier = (delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)GetVTableSlot(managerStatics, 7);
            CheckHr(createNotifier(managerStatics, hAppId, &notifier), "CreateToastNotifierWithId");

            // IToastNotifier.Show is slot 6.
            var show = (delegate* unmanaged[Stdcall]<nint, nint, int>)GetVTableSlot(notifier, 6);
            CheckHr(show(notifier, toast), "IToastNotifier.Show");
        }
        finally
        {
            ReleaseCom(notifier);
            ReleaseCom(managerStatics);
            ReleaseCom(expirationProp);
            ReleaseCom(propFactory);
            ReleaseCom(toast);
            ReleaseCom(toastFactory);
            ReleaseCom(xmlDocIo);
            ReleaseCom(xmlDoc);
            DeleteHString(hAppId);
            DeleteHString(hManagerClass);
            DeleteHString(hPropClass);
            DeleteHString(hToastClass);
            DeleteHString(hXmlText);
            DeleteHString(hXmlClass);
        }
    }

    private static nint GetVTableSlot(nint comObject, int slotIndex)
    {
        var vtable = *(nint**)comObject;
        return vtable[slotIndex];
    }

    private static int QueryInterface(nint comObject, Guid* iid, nint* result)
    {
        var qi = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)GetVTableSlot(comObject, 0);
        return qi(comObject, iid, result);
    }

    private static void ReleaseCom(nint comObject)
    {
        if (comObject == 0)
        {
            return;
        }

        var release = (delegate* unmanaged[Stdcall]<nint, uint>)GetVTableSlot(comObject, 2);
        release(comObject);
    }

    private static nint CreateHString(string value)
    {
        nint hstring = 0;
        CheckHr(WindowsCreateString(value, (uint)value.Length, &hstring), "WindowsCreateString");
        return hstring;
    }

    private static void DeleteHString(nint hstring)
    {
        if (hstring != 0)
        {
            _ = WindowsDeleteString(hstring);
        }
    }

    private static void CheckHr(int hr, string operation)
    {
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
            throw new COMException($"{operation} failed with HRESULT 0x{hr:X8}.", hr);
        }
    }

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoActivateInstance(nint activatableClassId, nint* instance);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(nint activatableClassId, Guid* iid, nint* factory);

    [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string sourceString, uint length, nint* hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(nint hstring);

    [DllImport("shell32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    private sealed class ToastContentBuilder
    {
        private readonly List<string> lines = [];

        public ToastContentBuilder AddText(string text)
        {
            if (!string.IsNullOrEmpty(text))
            {
                lines.Add(text);
            }

            return this;
        }

        public string GetXml()
        {
            var sb = new StringBuilder(256);
            sb.Append("<toast><visual><binding template=\"ToastGeneric\">");
            foreach (var line in lines)
            {
                sb.Append("<text>");
                sb.Append(SecurityElement.Escape(line));
                sb.Append("</text>");
            }

            sb.Append("</binding></visual></toast>");
            return sb.ToString();
        }
    }
}
