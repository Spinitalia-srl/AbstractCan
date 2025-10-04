using SocketCANSharp;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Peak.Can.Basic.BackwardCompatibility;
using TPCANHandle = System.UInt16;

namespace AbstractCAN
{
    public interface ICan : IDisposable
    {
        int send(CanFrame frame);
        bool get_messages(out ConcurrentQueue<CanFrame> canFrames);
        bool IsRunning();
        bool IsAlive();
        void start_reading();
        void stop_reading();
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
            Name = _name;
            _mSckt = LibcNativeMethods.Socket(SocketCanConstants.PF_CAN, SocketType.Raw, SocketCanProtocolType.CAN_RAW);
            var ifr = new Ifreq(_name);
            int ioctlResult = LibcNativeMethods.Ioctl(_mSckt, SocketCanConstants.SIOCGIFINDEX, ifr);
            int flags = Fcntl(_mSckt, F_GETFL, 0);
            int res = Fcntl(_mSckt, F_SETFL, flags | O_NONBLOCK);

            var addr = new SockAddrCan(ifr.IfIndex);
            int bindResult = LibcNativeMethods.Bind(_mSckt, addr, Marshal.SizeOf(typeof(SockAddrCan)));
            if(_read) start_reading();
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
            Running = true;
            while (_mAlive)
            {
                var readFrame = new CanFrame();
                int res = LibcNativeMethods.Read(_mSckt, ref readFrame, Marshal.SizeOf(typeof(CanFrame)));
                if (res > 0)
                {
                    _mMsgQueue.Enqueue(readFrame);
                }
            }
            Running = false;
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

        public bool IsAlive()
        {
            return Alive;
        }

        public bool IsRunning()
        {
            return Running;
        }

        private Task? _mReadTask;
        private ConcurrentQueue<CanFrame> _mMsgQueue = new();
        private bool _mAlive = false;
        private bool _mRunning = false;
        public bool Alive { get => _mAlive; private set => _mAlive = value; }
        public bool Running {get => _mRunning; private set => _mRunning = value; }
        private String _mName = "";
        public String Name { get => _mName; set => _mName = value; }
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
        private bool _mRunning;
        private bool _mActive;
        public bool Active { get => _mActive; private set => _mActive = value; }
        public WCan()
        {
            startCan(); // true listen only
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

        public bool get_messages_old(out ConcurrentQueue<CanFrame> canFrames)
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

        public bool get_messages(out ConcurrentQueue<CanFrame> canFrames)
        {
            canFrames = new ConcurrentQueue<CanFrame>();

            while (true)
            {
                // Try to read one message
                var status = PCANBasic.Read(_mHandle, out TPCANMsg tmp);

                if (status == TPCANStatus.PCAN_ERROR_QRCVEMPTY)
                {
                    // Queue empty → done
                    break;
                }
                else if (status == TPCANStatus.PCAN_ERROR_OK)
                {
                    // Create a new frame for this message
                    var frame = new CanFrame
                    {
                        Data = tmp.DATA,
                        Length = tmp.LEN,
                        CanId = tmp.ID | ((tmp.MSGTYPE == TPCANMessageType.PCAN_MESSAGE_EXTENDED) ? 0x80000000 : 0x00)
                    };
                    canFrames.Enqueue(frame);
                    return true;
                }
                else
                {
                    //Console.WriteLine($"Error while reading CAN message: {status}");
                    return false;
                }
            }

            return true;
        }

        public bool IsRunning()
        {
            return _mRunning;
        }

        public bool IsAlive()
        {
            return Active;
        }

        public void start_reading()
        {
            startCan();
        }

        public void stop_reading()
        {
            _mHandle = 0x51;
            _mTpcanStatus = PCANBasic.Uninitialize(_mHandle);
        }

        public bool startCan(bool listenOnly = false)
        {
            _mHandle = PCANBasic.PCAN_USBBUS1; // 0x51;
            _mTpcanStatus = PCANBasic.Uninitialize(_mHandle);
            // --- Listen-only mode (same effect as the checkbox in PCAN-View) ---
            if (listenOnly)
            {
                uint on = PCANBasic.PCAN_PARAMETER_ON; // 0x01
                _mTpcanStatus = PCANBasic.SetValue(
                    _mHandle,
                    TPCANParameter.PCAN_LISTEN_ONLY,
                    ref on,
                    sizeof(uint));

                if (_mTpcanStatus != TPCANStatus.PCAN_ERROR_OK)
                {
                    Console.WriteLine($"PCAN_LISTEN_ONLY failed: {_mTpcanStatus}");
                    // You can decide to return false or continue in normal mode
                }

                
            }


            _mTpcanStatus = PCANBasic.Initialize(_mHandle, _mBaudrate);
            
            _mTpcanStatus = PCANBasic.Reset(_mHandle);
            if (_mTpcanStatus == TPCANStatus.PCAN_ERROR_OK)
            {
                _mRunning = true;
                Active = true;
            }
            else
            {
                _mRunning = false;
                Active = false;
            }
            return _mRunning;
        }

        public void Dispose()
        {
            get_messages(out ConcurrentQueue<CanFrame> _unused);
            _mRunning = false;
            Active = false;
        }
    }
}