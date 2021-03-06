// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

/*=============================================================================
**
**
**
** Purpose: Domains represent an application within the runtime. Objects can 
**          not be shared between domains and each domain can be configured
**          independently. 
**
**
=============================================================================*/

namespace System
{
    using System;
    using System.Reflection;
    using System.Runtime;
    using System.Runtime.CompilerServices;
    using System.Security;
    using System.Collections;
    using System.Collections.Generic;
    using System.Threading;
    using System.Runtime.InteropServices;
    using System.Reflection.Emit;
    using CultureInfo = System.Globalization.CultureInfo;
    using System.IO;
    using AssemblyHashAlgorithm = System.Configuration.Assemblies.AssemblyHashAlgorithm;
    using System.Text;
    using System.Runtime.ConstrainedExecution;
    using System.Runtime.Versioning;
    using System.Diagnostics;
    using System.Diagnostics.Contracts;
    using System.Runtime.ExceptionServices;

    internal sealed class AppDomain
    {
        // Domain security information
        // These fields initialized from the other side only. (NOTE: order 
        // of these fields cannot be changed without changing the layout in 
        // the EE- AppDomainBaseObject in this case)

        private AppDomainManager _domainManager;
        private Dictionary<String, Object> _LocalStore;
        private AppDomainSetup _FusionStore;
        public event AssemblyLoadEventHandler AssemblyLoad;

        private ResolveEventHandler _TypeResolve;

        public event ResolveEventHandler TypeResolve
        {
            add
            {
                lock (this)
                {
                    _TypeResolve += value;
                }
            }

            remove
            {
                lock (this)
                {
                    _TypeResolve -= value;
                }
            }
        }

        private ResolveEventHandler _ResourceResolve;

        public event ResolveEventHandler ResourceResolve
        {
            add
            {
                lock (this)
                {
                    _ResourceResolve += value;
                }
            }

            remove
            {
                lock (this)
                {
                    _ResourceResolve -= value;
                }
            }
        }

        private ResolveEventHandler _AssemblyResolve;

        public event ResolveEventHandler AssemblyResolve
        {
            add
            {
                lock (this)
                {
                    _AssemblyResolve += value;
                }
            }

            remove
            {
                lock (this)
                {
                    _AssemblyResolve -= value;
                }
            }
        }


        private EventHandler _processExit;

        private EventHandler _domainUnload;

        private UnhandledExceptionEventHandler _unhandledException;

        // The compat flags are set at domain creation time to indicate that the given breaking
        // changes (named in the strings) should not be used in this domain. We only use the 
        // keys, the vhe values are ignored.
        private Dictionary<String, object> _compatFlags;

        // Delegate that will hold references to FirstChance exception notifications
        private EventHandler<FirstChanceExceptionEventArgs> _firstChanceException;

        private IntPtr _pDomain;                      // this is an unmanaged pointer (AppDomain * m_pDomain)` used from the VM.

        private bool _compatFlagsInitialized;

        internal const String TargetFrameworkNameAppCompatSetting = "TargetFrameworkName";

#if FEATURE_APPX
        private static APPX_FLAGS s_flags;

        //
        // Keep in async with vm\appdomainnative.cpp
        //
        [Flags]
        private enum APPX_FLAGS
        {
            APPX_FLAGS_INITIALIZED = 0x01,

            APPX_FLAGS_APPX_MODEL = 0x02,
            APPX_FLAGS_APPX_DESIGN_MODE = 0x04,
            APPX_FLAGS_APPX_MASK = APPX_FLAGS_APPX_MODEL |
                                            APPX_FLAGS_APPX_DESIGN_MODE,
        }

        private static APPX_FLAGS Flags
        {
            get
            {
                if (s_flags == 0)
                    s_flags = nGetAppXFlags();

                Debug.Assert(s_flags != 0);
                return s_flags;
            }
        }
#endif // FEATURE_APPX

#if FEATURE_APPX
        [DllImport(JitHelpers.QCall, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.I4)]
        private static extern APPX_FLAGS nGetAppXFlags();
#endif

        /// <summary>
        ///     Get a handle used to make a call into the VM pointing to this domain
        /// </summary>
        internal AppDomainHandle GetNativeHandle()
        {
            // This should never happen under normal circumstances. However, there ar ways to create an
            // uninitialized object through remoting, etc.
            if (_pDomain == IntPtr.Zero)
            {
                throw new InvalidOperationException(SR.Argument_InvalidHandle);
            }

            return new AppDomainHandle(_pDomain);
        }

        /// <summary>
        ///     If this AppDomain is configured to have an AppDomain manager then create the instance of it.
        ///     This method is also called from the VM to create the domain manager in the default domain.
        /// </summary>
        private void CreateAppDomainManager()
        {
            Debug.Assert(_domainManager == null, "_domainManager == null");

            AppDomainSetup adSetup = FusionStore;
            String trustedPlatformAssemblies = (String)(GetData("TRUSTED_PLATFORM_ASSEMBLIES"));
            if (trustedPlatformAssemblies != null)
            {
                String platformResourceRoots = (String)(GetData("PLATFORM_RESOURCE_ROOTS"));
                if (platformResourceRoots == null)
                {
                    platformResourceRoots = String.Empty;
                }

                String appPaths = (String)(GetData("APP_PATHS"));
                if (appPaths == null)
                {
                    appPaths = String.Empty;
                }

                String appNiPaths = (String)(GetData("APP_NI_PATHS"));
                if (appNiPaths == null)
                {
                    appNiPaths = String.Empty;
                }

                String appLocalWinMD = (String)(GetData("APP_LOCAL_WINMETADATA"));
                if (appLocalWinMD == null)
                {
                    appLocalWinMD = String.Empty;
                }
                SetupBindingPaths(trustedPlatformAssemblies, platformResourceRoots, appPaths, appNiPaths, appLocalWinMD);
            }

            InitializeCompatibilityFlags();
        }

        /// <summary>
        ///     Initialize the compatibility flags to non-NULL values.
        ///     This method is also called from the VM when the default domain doesn't have a domain manager.
        /// </summary>
        private void InitializeCompatibilityFlags()
        {
            AppDomainSetup adSetup = FusionStore;

            // set up shim flags regardless of whether we create a DomainManager in this method.
            if (adSetup.GetCompatibilityFlags() != null)
            {
                _compatFlags = new Dictionary<String, object>(adSetup.GetCompatibilityFlags(), StringComparer.OrdinalIgnoreCase);
            }

            // for perf, we don't intialize the _compatFlags dictionary when we don't need to.  However, we do need to make a 
            // note that we've run this method, because IsCompatibilityFlagsSet needs to return different values for the
            // case where the compat flags have been setup.
            Debug.Assert(!_compatFlagsInitialized);
            _compatFlagsInitialized = true;
        }

        /// <summary>
        ///     Returns whether the current AppDomain follows the AppX rules.
        /// </summary>
        [Pure]
        internal static bool IsAppXModel()
        {
#if FEATURE_APPX
            return (Flags & APPX_FLAGS.APPX_FLAGS_APPX_MODEL) != 0;
#else
            return false;
#endif
        }

        /// <summary>
        ///     Returns the setting of the AppXDevMode config switch.
        /// </summary>
        [Pure]
        internal static bool IsAppXDesignMode()
        {
#if FEATURE_APPX
            return (Flags & APPX_FLAGS.APPX_FLAGS_APPX_MASK) == (APPX_FLAGS.APPX_FLAGS_APPX_MODEL | APPX_FLAGS.APPX_FLAGS_APPX_DESIGN_MODE);
#else
            return false;
#endif
        }

        /// <summary>
        ///     Checks (and throws on failure) if the domain supports Assembly.LoadFrom.
        /// </summary>
        [Pure]
        internal static void CheckLoadFromSupported()
        {
#if FEATURE_APPX
            if (IsAppXModel())
                throw new NotSupportedException(SR.Format(SR.NotSupported_AppX, "Assembly.LoadFrom"));
#endif
        }

        /// <summary>
        ///     Checks (and throws on failure) if the domain supports Assembly.LoadFile.
        /// </summary>
        [Pure]
        internal static void CheckLoadFileSupported()
        {
#if FEATURE_APPX
            if (IsAppXModel())
                throw new NotSupportedException(SR.Format(SR.NotSupported_AppX, "Assembly.LoadFile"));
#endif
        }

        /// <summary>
        ///     Checks (and throws on failure) if the domain supports Assembly.Load(byte[] ...).
        /// </summary>
        [Pure]
        internal static void CheckLoadByteArraySupported()
        {
#if FEATURE_APPX
            if (IsAppXModel())
                throw new NotSupportedException(SR.Format(SR.NotSupported_AppX, "Assembly.Load(byte[], ...)"));
#endif
        }

        public AppDomainManager DomainManager
        {
            get
            {
                return _domainManager;
            }
        }

        public static AppDomain CurrentDomain
        {
            get
            {
                return Thread.GetDomain();
            }
        }

        public String BaseDirectory
        {
            get
            {
                return FusionStore.ApplicationBase;
            }
        }

        public override String ToString()
        {
            StringBuilder sb = StringBuilderCache.Acquire();

            String fn = nGetFriendlyName();
            if (fn != null)
            {
                sb.Append(SR.Loader_Name + fn);
                sb.Append(Environment.NewLine);
            }

            return StringBuilderCache.GetStringAndRelease(sb);
        }

        [MethodImpl(MethodImplOptions.InternalCall)]
        private extern Assembly[] nGetAssemblies(bool forIntrospection);

        internal Assembly[] GetAssemblies(bool forIntrospection)
        {
            return nGetAssemblies(forIntrospection);
        }

        // this is true when we've removed the handles etc so really can't do anything
        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        internal extern bool IsUnloadingForcedFinalize();

        // this is true when we've just started going through the finalizers and are forcing objects to finalize
        // so must be aware that certain infrastructure may have gone away
        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        public extern bool IsFinalizingForUnload();

        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        internal static extern void PublishAnonymouslyHostedDynamicMethodsAssembly(RuntimeAssembly assemblyHandle);

        public void SetData(string name, object data)
        {
            if (name == null)
                throw new ArgumentNullException(nameof(name));

            lock (((ICollection)LocalStore).SyncRoot)
            {
                LocalStore[name] = data;
            }
        }

        [Pure]
        public Object GetData(string name)
        {
            if (name == null)
                throw new ArgumentNullException(nameof(name));

            object data;
            lock (((ICollection)LocalStore).SyncRoot)
            {
                LocalStore.TryGetValue(name, out data);
            }
            if (data == null)
                return null;
            return data;
        }

        [Obsolete("AppDomain.GetCurrentThreadId has been deprecated because it does not provide a stable Id when managed threads are running on fibers (aka lightweight threads). To get a stable identifier for a managed thread, use the ManagedThreadId property on Thread.  http://go.microsoft.com/fwlink/?linkid=14202", false)]
        [DllImport(Interop.Libraries.Kernel32)]
        public static extern int GetCurrentThreadId();

        private AppDomain()
        {
            throw new NotSupportedException(SR.NotSupported_Constructor);
        }

        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        internal extern void nCreateContext();

        [DllImport(JitHelpers.QCall, CharSet = CharSet.Unicode)]
        private static extern void nSetupBindingPaths(String trustedPlatformAssemblies, String platformResourceRoots, String appPath, String appNiPaths, String appLocalWinMD);

        internal void SetupBindingPaths(String trustedPlatformAssemblies, String platformResourceRoots, String appPath, String appNiPaths, String appLocalWinMD)
        {
            nSetupBindingPaths(trustedPlatformAssemblies, platformResourceRoots, appPath, appNiPaths, appLocalWinMD);
        }

        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        private extern String nGetFriendlyName();

        // support reliability for certain event handlers, if the target
        // methods also participate in this discipline.  If caller passes
        // an existing MulticastDelegate, then we could use a MDA to indicate
        // that reliability is not guaranteed.  But if it is a single cast
        // scenario, we can make it work.

        public event EventHandler ProcessExit
        {
            add
            {
                if (value != null)
                {
                    RuntimeHelpers.PrepareContractedDelegate(value);
                    lock (this)
                        _processExit += value;
                }
            }
            remove
            {
                lock (this)
                    _processExit -= value;
            }
        }


        public event EventHandler DomainUnload
        {
            add
            {
                if (value != null)
                {
                    RuntimeHelpers.PrepareContractedDelegate(value);
                    lock (this)
                        _domainUnload += value;
                }
            }
            remove
            {
                lock (this)
                    _domainUnload -= value;
            }
        }


        public event UnhandledExceptionEventHandler UnhandledException
        {
            add
            {
                if (value != null)
                {
                    RuntimeHelpers.PrepareContractedDelegate(value);
                    lock (this)
                        _unhandledException += value;
                }
            }
            remove
            {
                lock (this)
                    _unhandledException -= value;
            }
        }

        // This is the event managed code can wireup against to be notified
        // about first chance exceptions. 
        //
        // To register/unregister the callback, the code must be SecurityCritical.
        public event EventHandler<FirstChanceExceptionEventArgs> FirstChanceException
        {
            add
            {
                if (value != null)
                {
                    RuntimeHelpers.PrepareContractedDelegate(value);
                    lock (this)
                        _firstChanceException += value;
                }
            }
            remove
            {
                lock (this)
                    _firstChanceException -= value;
            }
        }

        private void OnAssemblyLoadEvent(RuntimeAssembly LoadedAssembly)
        {
            AssemblyLoadEventHandler eventHandler = AssemblyLoad;
            if (eventHandler != null)
            {
                AssemblyLoadEventArgs ea = new AssemblyLoadEventArgs(LoadedAssembly);
                eventHandler(this, ea);
            }
        }

        // This method is called by the VM.
        private RuntimeAssembly OnResourceResolveEvent(RuntimeAssembly assembly, String resourceName)
        {
            return InvokeResolveEvent(_ResourceResolve, assembly, resourceName);
        }

        // This method is called by the VM
        private RuntimeAssembly OnTypeResolveEvent(RuntimeAssembly assembly, String typeName)
        {
            return InvokeResolveEvent(_TypeResolve, assembly, typeName);
        }

        // This method is called by the VM.
        private RuntimeAssembly OnAssemblyResolveEvent(RuntimeAssembly assembly, String assemblyFullName)
        {
            return InvokeResolveEvent(_AssemblyResolve, assembly, assemblyFullName);
        }

        private RuntimeAssembly InvokeResolveEvent(ResolveEventHandler eventHandler, RuntimeAssembly assembly, string name)
        {
            if (eventHandler == null)
                return null;

            foreach (ResolveEventHandler handler in eventHandler.GetInvocationList())
            {
                Assembly asm = handler(this, new ResolveEventArgs(name, assembly));
                RuntimeAssembly ret = GetRuntimeAssembly(asm);
                if (ret != null)
                    return ret;
            }

            return null;
        }

#if FEATURE_COMINTEROP
        // Called by VM - code:CLRPrivTypeCacheWinRT::RaiseDesignerNamespaceResolveEvent
        private string[] OnDesignerNamespaceResolveEvent(string namespaceName)
        {
            return System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeMetadata.OnDesignerNamespaceResolveEvent(this, namespaceName);
        }
#endif // FEATURE_COMINTEROP

        internal AppDomainSetup FusionStore
        {
            get
            {
                Debug.Assert(_FusionStore != null,
                                "Fusion store has not been correctly setup in this domain");
                return _FusionStore;
            }
        }

        internal static RuntimeAssembly GetRuntimeAssembly(Assembly asm)
        {
            if (asm == null)
                return null;

            RuntimeAssembly rtAssembly = asm as RuntimeAssembly;
            if (rtAssembly != null)
                return rtAssembly;

            AssemblyBuilder ab = asm as AssemblyBuilder;
            if (ab != null)
                return ab.InternalAssembly;

            return null;
        }

        private Dictionary<String, Object> LocalStore
        {
            get
            {
                if (_LocalStore != null)
                    return _LocalStore;
                else
                {
                    _LocalStore = new Dictionary<String, Object>();
                    return _LocalStore;
                }
            }
        }

        [DllImport(JitHelpers.QCall, CharSet = CharSet.Unicode)]
        private static extern void nSetNativeDllSearchDirectories(string paths);

        private void SetupFusionStore(AppDomainSetup info, AppDomainSetup oldInfo)
        {
            Debug.Assert(info != null);

            if (info.ApplicationBase == null)
            {
                info.SetupDefaults(RuntimeEnvironment.GetModuleFileName(), imageLocationAlreadyNormalized: true);
            }

            nCreateContext();

            // This must be the last action taken
            _FusionStore = info;
        }

        // Used to switch into other AppDomain and call SetupRemoteDomain.
        //   We cannot simply call through the proxy, because if there
        //   are any remoting sinks registered, they can add non-mscorlib
        //   objects to the message (causing an assembly load exception when
        //   we try to deserialize it on the other side)
        private static object PrepareDataForSetup(String friendlyName,
                                                        AppDomainSetup setup,
                                                        string[] propertyNames,
                                                        string[] propertyValues)
        {
            AppDomainSetup newSetup = new AppDomainSetup(setup, false);

            // Remove the special AppDomainCompatSwitch entries from the set of name value pairs
            // And add them to the AppDomainSetup
            //
            // This is only supported on CoreCLR through ICLRRuntimeHost2.CreateAppDomainWithManager
            // Desktop code should use System.AppDomain.CreateDomain() or 
            // System.AppDomainManager.CreateDomain() and add the flags to the AppDomainSetup
            List<String> compatList = new List<String>();

            if (propertyNames != null && propertyValues != null)
            {
                for (int i = 0; i < propertyNames.Length; i++)
                {
                    if (String.Compare(propertyNames[i], "AppDomainCompatSwitch", StringComparison.OrdinalIgnoreCase) == 0)
                    {
                        compatList.Add(propertyValues[i]);
                        propertyNames[i] = null;
                        propertyValues[i] = null;
                    }
                }

                if (compatList.Count > 0)
                {
                    newSetup.SetCompatibilitySwitches(compatList);
                }
            }

            return new Object[]
            {
                friendlyName,
                newSetup,
                propertyNames,
                propertyValues
            };
        } // PrepareDataForSetup

        private static Object Setup(Object arg)
        {
            Object[] args = (Object[])arg;
            String friendlyName = (String)args[0];
            AppDomainSetup setup = (AppDomainSetup)args[1];
            string[] propertyNames = (string[])args[2]; // can contain null elements
            string[] propertyValues = (string[])args[3]; // can contain null elements

            AppDomain ad = AppDomain.CurrentDomain;
            AppDomainSetup newSetup = new AppDomainSetup(setup, false);

            if (propertyNames != null && propertyValues != null)
            {
                for (int i = 0; i < propertyNames.Length; i++)
                {
                    // We want to set native dll probing directories before any P/Invokes have a
                    // chance to fire. The Path class, for one, has P/Invokes.
                    if (propertyNames[i] == "NATIVE_DLL_SEARCH_DIRECTORIES")
                    {
                        if (propertyValues[i] == null)
                            throw new ArgumentNullException("NATIVE_DLL_SEARCH_DIRECTORIES");

                        string paths = propertyValues[i];
                        if (paths.Length == 0)
                            break;

                        nSetNativeDllSearchDirectories(paths);
                    }
                }

                for (int i = 0; i < propertyNames.Length; i++)
                {
                    if (propertyNames[i] == "APPBASE") // make sure in sync with Fusion
                    {
                        if (propertyValues[i] == null)
                            throw new ArgumentNullException("APPBASE");

                        if (PathInternal.IsPartiallyQualified(propertyValues[i]))
                            throw new ArgumentException(SR.Argument_AbsolutePathRequired);

                        newSetup.ApplicationBase = NormalizePath(propertyValues[i], fullCheck: true);
                    }
                    else if (propertyNames[i] == "TRUSTED_PLATFORM_ASSEMBLIES" ||
                       propertyNames[i] == "PLATFORM_RESOURCE_ROOTS" ||
                       propertyNames[i] == "APP_PATHS" ||
                       propertyNames[i] == "APP_NI_PATHS")
                    {
                        string values = propertyValues[i];
                        if (values == null)
                            throw new ArgumentNullException(propertyNames[i]);

                        ad.SetData(propertyNames[i], NormalizeAppPaths(values));
                    }
                    else if (propertyNames[i] != null)
                    {
                        ad.SetData(propertyNames[i], propertyValues[i]);     // just propagate
                    }
                }
            }

            ad.SetupFusionStore(newSetup, null); // makes FusionStore a ref to newSetup

            // technically, we don't need this, newSetup refers to the same object as FusionStore 
            // but it's confusing since it isn't immediately obvious whether we have a ref or a copy
            AppDomainSetup adSetup = ad.FusionStore;

            // set up the friendly name
            ad.nSetupFriendlyName(friendlyName);

            ad.CreateAppDomainManager(); // could modify FusionStore's object

            return null;
        }

        private static string NormalizeAppPaths(string values)
        {
            int estimatedLength = values.Length + 1; // +1 for extra separator temporarily added at end
            StringBuilder sb = StringBuilderCache.Acquire(estimatedLength);

            for (int pos = 0; pos < values.Length; pos++)
            {
                string path;

                int nextPos = values.IndexOf(Path.PathSeparator, pos);
                if (nextPos == -1)
                {
                    path = values.Substring(pos);
                    pos = values.Length - 1;
                }
                else
                {
                    path = values.Substring(pos, nextPos - pos);
                    pos = nextPos;
                }

                // Skip empty directories
                if (path.Length == 0)
                    continue;

                if (PathInternal.IsPartiallyQualified(path))
                    throw new ArgumentException(SR.Argument_AbsolutePathRequired);

                string appPath = NormalizePath(path, fullCheck: true);
                sb.Append(appPath);
                sb.Append(Path.PathSeparator);
            }

            // Strip the last separator
            if (sb.Length > 0)
            {
                sb.Remove(sb.Length - 1, 1);
            }

            return StringBuilderCache.GetStringAndRelease(sb);
        }

        internal static string NormalizePath(string path, bool fullCheck)
        {
            return Path.GetFullPath(path);
        }

        // This routine is called from unmanaged code to
        // set the default fusion context.
        private void SetupDomain(bool allowRedirects, String path, String configFile, String[] propertyNames, String[] propertyValues)
        {
            // It is possible that we could have multiple threads initializing
            // the default domain. We will just take the winner of these two.
            // (eg. one thread doing a com call and another doing attach for IJW)
            lock (this)
            {
                if (_FusionStore == null)
                {
                    AppDomainSetup setup = new AppDomainSetup();

                    // always use internet permission set
                    SetupFusionStore(setup, null);
                }
            }
        }

        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        private extern void nSetupFriendlyName(string friendlyName);

        public AppDomainSetup SetupInformation
        {
            get
            {
                return new AppDomainSetup(FusionStore, true);
            }
        }

        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        internal extern String IsStringInterned(String str);

        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        internal extern String GetOrInternString(String str);

        public bool IsFullyTrusted
        {
            get
            {
                return true;
            }
        }

        public Int32 Id
        {
            get
            {
                return GetId();
            }
        }

        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        internal extern Int32 GetId();
    }

    /// <summary>
    ///     Handle used to marshal an AppDomain to the VM (eg QCall). When marshaled via a QCall, the target
    ///     method in the VM will receive a QCall::AppDomainHandle parameter.
    /// </summary>
    internal struct AppDomainHandle
    {
        private IntPtr m_appDomainHandle;

        // Note: generall an AppDomainHandle should not be directly constructed, instead the
        // code:System.AppDomain.GetNativeHandle method should be called to get the handle for a specific
        // AppDomain.
        internal AppDomainHandle(IntPtr domainHandle)
        {
            m_appDomainHandle = domainHandle;
        }
    }
}
