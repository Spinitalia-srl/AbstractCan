using System;
using SocketCANSharp;
using SocketCANSharp.Network;
using System.Threading;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Peak.Can.Basic.BackwardCompatibility;
using TPCANHandle = System.UInt16;
//using Microsoft.VisualBasic;
//using static System.Runtime.InteropServices.JavaScript.JSType;
using System.Reflection.Metadata;

namespace CANTester
{
    public interface ICan : IDisposable
    {
        int send(CanFrame frame);
        bool get_messages(out ConcurrentQueue<CanFrame> canFrames);
    }

    public class LCan : ICan
    {
        [DllImport("libc", EntryPoint = "fcntl")]
        private static extern int Fcntl(SafeFileDescriptorHandle fd, int ctl, int arg);

        private static int F_SETFL = 4;
        private static int F_GETFL = 3;
        private static int O_NONBLOCK = 0x0800;
        public LCan(string _name, bool _read = false)
        {
            Name = _name ?? "can";
            _mSckt = LibcNativeMethods.Socket(SocketCanConstants.PF_CAN, SocketType.Raw, SocketCanProtocolType.CAN_RAW);
            var ifr = new Ifreq(_name);
            int ioctlResult = LibcNativeMethods.Ioctl(_mSckt, SocketCanConstants.SIOCGIFINDEX, ifr);
            int flags = Fcntl(_mSckt, F_GETFL, 0);
            int res = Fcntl(_mSckt, F_SETFL, flags | O_NONBLOCK);

            var addr = new SockAddrCan(ifr.IfIndex);
            int bindResult = LibcNativeMethods.Bind(_mSckt, addr, Marshal.SizeOf(typeof(SockAddrCan)));
            start_reading();
        }

        public void Dispose()
        {
            stop_reading();
            _mMsgQueue.Clear();
        }

        public int send(CanFrame frame)
        {
            return LibcNativeMethods.Write(_mSckt, ref frame, Marshal.SizeOf(typeof(CanFrame)));
            //The function returns the number of bytes written or - 1 if an error occurs. If - 1 is returned, further error information can typically be retrieved using errno.
        }

        public bool get_messages(out ConcurrentQueue<CanFrame> canFrames)
        {
            canFrames = new ConcurrentQueue<CanFrame>();
            if (!_mMsgQueue.IsEmpty)
            {
                while (_mMsgQueue.TryDequeue(out CanFrame msg))
                {
                    canFrames.Enqueue(msg);
                }
                return true;
            }
            return false;
        }

        private void read()
        {
            _mRunning = true;
            while (_mAlive)
            {
                var readFrame = new CanFrame();
                int res = LibcNativeMethods.Read(_mSckt, ref readFrame, Marshal.SizeOf(typeof(CanFrame)));
                if (res > 0)
                {
                    _mMsgQueue.Enqueue(readFrame);
                }
            }
            _mRunning = false;
        }

        public void start_reading()
        {
            _mAlive = true;
            _mReadTask = new Task(() => read());
            _mReadTask.Start();
        }

        public void stop_reading()
        {
            _mAlive = false;
            _mReadTask?.Wait();
        }
        private Task? _mReadTask;
        private ConcurrentQueue<CanFrame> _mMsgQueue = new();
        private bool _mAlive = false;
        private bool _mRunning = false;
        private System.String _mName;
        public System.String Name { get => _mName; set => _mName = value; }
        private SafeFileDescriptorHandle? _mSckt;

        private void ReleaseUnmanagedResources()
        {
            // TODO release unmanaged resources here
        }
    }

    public class WCan : ICan
    {
        private TPCANHandle _mHandle;
        private TPCANBaudrate _mBaudrate = TPCANBaudrate.PCAN_BAUD_250K;
        private TPCANStatus _mTpcanStatus = new TPCANStatus();
        private bool _mConnected;
        private bool _mActive;
        public bool Active { get => _mActive; private set => _mActive = value; }
        public WCan()
        {
            startCan();
        }
        public int send(CanFrame frame)
        {
            int res = 0;
            TPCANMsg msg = new();
            msg.ID = frame.CanId;
            msg.MSGTYPE = (frame.CanId & 0x80000000) != 0
                ? TPCANMessageType.PCAN_MESSAGE_EXTENDED
                : TPCANMessageType.PCAN_MESSAGE_STANDARD;
            msg.DATA = frame.Data;
            msg.LEN = frame.Length;
            res= (int)PCANBasic.Write(_mHandle, ref msg);
            return (-res); // - for linux compatibility
            //PCAN_ERROR_XMTFULL(0x00002)
            //PCAN_ERROR_BUSLIGHT(0x00004)
            //PCAN_ERROR_BUSHEAVY(0x00008)
            //    PCAN_ERROR_BUSOFF(0x00010)
            //PCAN_ERROR_ILLHANDLE(0x00400)
            //PCAN_ERROR_INITIALIZE(0x00040)
        }

        public bool get_messages(out ConcurrentQueue<CanFrame> canFrames)
        {
            CanFrame placeholder = new();
            canFrames = new ConcurrentQueue<CanFrame>();
            if (_mTpcanStatus == TPCANStatus.PCAN_ERROR_OK || _mTpcanStatus == TPCANStatus.PCAN_ERROR_QRCVEMPTY)
            {
                do
                {
                    _mTpcanStatus = PCANBasic.Read(_mHandle, out TPCANMsg tmp);
                    placeholder.Data = tmp.DATA;
                    placeholder.Length = tmp.LEN;
                    placeholder.CanId = tmp.ID | ((tmp.MSGTYPE == TPCANMessageType.PCAN_MESSAGE_EXTENDED) ? 0x80000000 : 0x00);
                    canFrames.Enqueue(placeholder);
                } while ((_mTpcanStatus & TPCANStatus.PCAN_ERROR_QRCVEMPTY) != TPCANStatus.PCAN_ERROR_QRCVEMPTY);
            }
            else return false;
            return true;
        }

        public bool startCan()
        {
            _mHandle = 0x51;
            _mTpcanStatus = PCANBasic.Uninitialize(_mHandle);
            _mTpcanStatus = PCANBasic.Initialize(_mHandle, _mBaudrate);
            _mTpcanStatus = PCANBasic.Reset(PCANBasic.PCAN_USBBUS1);
            if (_mTpcanStatus == TPCANStatus.PCAN_ERROR_OK)
            {
                _mConnected = true;
                Active = true;
            }
            else
            {
                _mConnected = false;
                Active = false;
            }
            return _mConnected;
        }

        public void Dispose()
        {
            get_messages(out ConcurrentQueue<CanFrame> _unused);
            _mConnected = false;
            Active = false;
        }
    }
}