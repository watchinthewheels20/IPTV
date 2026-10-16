using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace LoungeTV.Controls;

public enum GamepadCommand { Up, Down, Left, Right, Select, Back, Fullscreen, ShiftEarlier, ShiftLater }

/// <summary>Basic Xbox/XInput navigation. Text entry still requires a keyboard.</summary>
public sealed class GamepadInput : IDisposable
{
    private const ushort Up = 0x0001, Down = 0x0002, Left = 0x0004, Right = 0x0008;
    private const ushort A = 0x1000, B = 0x2000, X = 0x4000, LB = 0x0100, RB = 0x0200;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private readonly Dictionary<ushort, DateTime> _nextRepeat = new();
    private ushort _lastButtons;
    private bool _available = true;
    private uint _activeController = uint.MaxValue;
    private DateTime _nextSearch = DateTime.MinValue;

    public event Action<GamepadCommand>? Command;

    public GamepadInput()
    {
        _timer.Tick += (_, _) => Poll();
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    private void Poll()
    {
        if (!_available) return;
        try
        {
            var found = false;
            XInputState state = default;
            // Most frames query only the connected pad. Scanning four slots every
            // frame is wasteful, especially with no controller connected.
            if (_activeController != uint.MaxValue && XInputGetState(_activeController, out state) == 0)
            {
                found = true;
            }
            else
            {
                _activeController = uint.MaxValue;
                if (DateTime.UtcNow >= _nextSearch)
                {
                    for (uint i = 0; i < 4; i++)
                    {
                        if (XInputGetState(i, out state) != 0) continue;
                        _activeController = i;
                        found = true;
                        break;
                    }
                    _nextSearch = DateTime.UtcNow.AddMilliseconds(found ? 0 : 500);
                }
            }
            var buttons = found ? state.Gamepad.Buttons : (ushort)0;
            Emit(buttons, Up, GamepadCommand.Up, true);
            Emit(buttons, Down, GamepadCommand.Down, true);
            Emit(buttons, Left, GamepadCommand.Left, true);
            Emit(buttons, Right, GamepadCommand.Right, true);
            Emit(buttons, A, GamepadCommand.Select, false);
            Emit(buttons, B, GamepadCommand.Back, false);
            Emit(buttons, X, GamepadCommand.Fullscreen, false);
            Emit(buttons, LB, GamepadCommand.ShiftEarlier, false);
            Emit(buttons, RB, GamepadCommand.ShiftLater, false);
            _lastButtons = buttons;
        }
        catch (DllNotFoundException) { _available = false; Stop(); }
        catch (EntryPointNotFoundException) { _available = false; Stop(); }
    }

    private void Emit(ushort buttons, ushort mask, GamepadCommand command, bool canRepeat)
    {
        if ((buttons & mask) == 0) { _nextRepeat.Remove(mask); return; }
        var first = (_lastButtons & mask) == 0;
        if (!first && (!canRepeat || (_nextRepeat.TryGetValue(mask, out var next) && DateTime.UtcNow < next)))
            return;
        _nextRepeat[mask] = DateTime.UtcNow.AddMilliseconds(first ? 195 : 85);
        Command?.Invoke(command);
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState", CallingConvention = CallingConvention.Winapi)]
    private static extern uint XInputGetState(uint userIndex, out XInputState state);

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    public void Dispose() => Stop();
}
