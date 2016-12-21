// --------------------------------------------------------------------------------------------------------------------
// <copyright company="Chocolatey" file="Job.cs">
//   Copyright 2014 - Present Rob Reynolds, the maintainers of Chocolatey, and RealDimensions Software, LLC
// </copyright>
// --------------------------------------------------------------------------------------------------------------------

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

// ReSharper disable InconsistentNaming
namespace ChocolateyGui.Utilities
{
    /// <summary>
    /// Major kudos to http://stackoverflow.com/a/36648201
    /// </summary>
    public class Job : IDisposable
    {
        private IntPtr _handle;
        private bool _disposed;

        public Job()
        {
            _handle = CreateJobObject(IntPtr.Zero, null);

            var info = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = 0x2000
            };

            var extendedInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = info
            };

            var length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            var extendedInfoPtr = Marshal.AllocHGlobal(length);
            Marshal.StructureToPtr(extendedInfo, extendedInfoPtr, false);

            if (
                !SetInformationJobObject(
                    _handle,
                    JobObjectInfoType.ExtendedLimitInformation,
                    extendedInfoPtr,
                    (uint)length))
            {
                throw new Exception($"Unable to set information.  Error: {Marshal.GetLastWin32Error()}");
            }
        }

        public void Close()
        {
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }

        public bool AddProcess(IntPtr processHandle)
        {
            return AssignProcessToJobObject(_handle, processHandle);
        }

        public bool AddProcess(int processId)
        {
            return AddProcess(Process.GetProcessById(processId).Handle);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
            }

            Close();
            _disposed = true;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr a, string name);

        [DllImport("kernel32.dll")]
        private static extern bool SetInformationJobObject(IntPtr job, JobObjectInfoType infoType, IntPtr jobObjectInfo, uint jobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr @object);
    }

    #region Helper classes
    
    [StructLayout(LayoutKind.Sequential)]
    public struct SECURITY_ATTRIBUTES
    {
        public uint Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    public enum JobObjectInfoType
    {
        AssociateCompletionPortInformation = 7,
        BasicLimitInformation = 2,
        BasicUIRestrictions = 4,
        EndOfJobTimeInformation = 6,
        ExtendedLimitInformation = 9,
        SecurityLimitInformation = 5,
        GroupInformation = 11
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    #endregion
}
