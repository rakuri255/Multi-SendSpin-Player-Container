using System.Runtime.InteropServices;
using MultiRoomAudio.Models.TriggerModels;
using System.Device.Gpio;
using System.Device.Gpio.Drivers;

namespace MultiRoomAudio.Relay.Gpio;

/// <summary>
/// Relay board implementation using Raspberry Pi GPIO via the Linux GPIO character device.
/// Each relay channel is mapped to a BCM GPIO pin number.
/// Requires /dev/gpiochipN to be accessible (Docker: mount -v /dev/gpiochip0:/dev/gpiochip0).
/// </summary>
public sealed class GpioRelayBoard : IRelayBoard
{
    private readonly int _chipIndex;
    private int _channelCount;
    private readonly int[] _channelStates = new int[16]; // cached software state per channel (1-based)
    private Dictionary<int, int> _pinMapping = new(); // channel (1-based) → BCM pin number
    private GpioController? _controller;
    private bool _isConnected;
    private bool _disposed;
    private readonly ILogger<GpioRelayBoard>? _logger;
    private readonly object _lock = new();

    /// <summary>
    /// Create a GPIO relay board for the given chip index and channel count.
    /// </summary>
    /// <param name="chipIndex">GPIO chip index (0 = /dev/gpiochip0).</param>
    /// <param name="channelCount">Number of relay channels (1-16).</param>
    /// <param name="logger">Optional logger.</param>
    public GpioRelayBoard(int chipIndex, int channelCount, ILogger<GpioRelayBoard>? logger = null)
    {
        _chipIndex = chipIndex;
        _channelCount = Math.Clamp(channelCount, 1, 16);
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsConnected => _isConnected;

    /// <inheritdoc />
    public string SerialNumber => $"gpiochip{_chipIndex}";

    /// <inheritdoc />
    public int ChannelCount => _channelCount;

    /// <inheritdoc />
    public int CurrentState
    {
        get
        {
            int state = 0;
            for (int i = 0; i < _channelCount; i++)
            {
                if (_channelStates[i] == 1)
                    state |= (1 << i);
            }
            return state;
        }
    }

    // ─── Static helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns true if any GPIO chip device is available on this system.
    /// </summary>
    public static bool IsAvailable()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return false;
        for (int i = 0; i <= 4; i++)
        {
            if (File.Exists($"/dev/gpiochip{i}"))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Enumerate all accessible GPIO chip devices on the system.
    /// </summary>
    public static List<GpioChipInfo> EnumerateChips(ILogger? logger = null)
    {
        var chips = new List<GpioChipInfo>();

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            logger?.LogDebug("GPIO enumeration skipped: not running on Linux");
            return chips;
        }

        for (int i = 0; i <= 4; i++)
        {
            var path = $"/dev/gpiochip{i}";
            if (!File.Exists(path))
                continue;

            try
            {
                // Verify accessibility by checking file attributes (throws if not accessible)
                _ = File.GetAttributes(path);
                chips.Add(new GpioChipInfo(i, path, $"Raspberry Pi GPIO Header (gpiochip{i})"));
                logger?.LogDebug("Found GPIO chip: {Path}", path);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "GPIO chip {Path} exists but is not accessible", path);
            }
        }

        return chips;
    }

    // ─── Connection ──────────────────────────────────────────────────────────

    /// <inheritdoc />
    public bool Open()
    {
        if (_disposed)
            return false;

        lock (_lock)
        {
            try
            {
                var driver = new LibGpiodDriver(_chipIndex);
                _controller = new GpioController(PinNumberingScheme.Logical, driver);
                _isConnected = true;

                // Open all already-mapped pins
                OpenMappedPins();

                _logger?.LogInformation(
                    "GPIO relay board opened: chip=gpiochip{ChipIndex}, {Channels} channels, {Mapped} pins mapped",
                    _chipIndex, _channelCount, _pinMapping.Count);

                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex,
                    "Failed to open GPIO chip gpiochip{ChipIndex}. " +
                    "Ensure /dev/gpiochip{ChipIndexDevice} is accessible (Docker: add --device /dev/gpiochip0)",
                    _chipIndex, _chipIndex);

                _controller?.Dispose();
                _controller = null;
                _isConnected = false;
                return false;
            }
        }
    }

    /// <inheritdoc />
    public bool OpenBySerial(string serialNumber)
    {
        // GPIO boards are opened by chip index, not serial number
        return Open();
    }

    /// <inheritdoc />
    public void Close()
    {
        lock (_lock)
        {
            if (_controller != null)
            {
                CloseAllPins();
                _controller.Dispose();
                _controller = null;
            }
            _isConnected = false;
        }
    }

    // ─── Pin mapping ─────────────────────────────────────────────────────────

    /// <summary>
    /// Update the channel-to-pin mapping for this GPIO relay board.
    /// Already-connected boards will have their pins closed/reopened automatically.
    /// </summary>
    /// <param name="mapping">Dictionary of channel (1-based) → BCM GPIO pin number.</param>
    public void UpdatePinMapping(Dictionary<int, int> mapping)
    {
        lock (_lock)
        {
            if (_controller != null && _isConnected)
            {
                // Close pins that are no longer in the mapping
                foreach (var (_, pin) in _pinMapping)
                {
                    try
                    {
                        if (_controller.IsPinOpen(pin) && !mapping.ContainsValue(pin))
                            _controller.ClosePin(pin);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "Error closing GPIO pin {Pin}", pin);
                    }
                }
            }

            _pinMapping = new Dictionary<int, int>(mapping);

            if (_controller != null && _isConnected)
            {
                OpenMappedPins();
            }

            _logger?.LogDebug("GPIO pin mapping updated: {Count} channels mapped", _pinMapping.Count);
        }
    }

    // ─── Relay control ───────────────────────────────────────────────────────

    /// <inheritdoc />
    public bool SetRelay(int channel, bool on)
    {
        if (!_isConnected || _controller == null)
            return false;

        if (channel < 1 || channel > _channelCount)
            return false;

        lock (_lock)
        {
            if (!_pinMapping.TryGetValue(channel, out var pin))
            {
                _logger?.LogWarning(
                    "GPIO relay board gpiochip{ChipIndex}: no BCM pin assigned to channel {Channel}. " +
                    "Configure the pin number in the trigger settings.",
                    _chipIndex, channel);
                return false;
            }

            try
            {
                _controller.Write(pin, on ? PinValue.High : PinValue.Low);
                _channelStates[channel - 1] = on ? 1 : 0;

                _logger?.LogDebug(
                    "GPIO gpiochip{ChipIndex} BCM{Pin} (CH{Channel}) → {State}",
                    _chipIndex, pin, channel, on ? "HIGH" : "LOW");

                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex,
                    "Failed to write GPIO pin BCM{Pin} (channel {Channel}) on gpiochip{ChipIndex}",
                    pin, channel, _chipIndex);
                return false;
            }
        }
    }

    /// <inheritdoc />
    public RelayState GetRelay(int channel)
    {
        if (channel < 1 || channel > _channelCount)
            return RelayState.Unknown;

        if (!_pinMapping.ContainsKey(channel))
            return RelayState.Unknown; // Pin not assigned yet

        return _channelStates[channel - 1] == 1 ? RelayState.On : RelayState.Off;
    }

    /// <inheritdoc />
    public bool AllOff()
    {
        if (!_isConnected || _controller == null)
            return false;

        bool allOk = true;

        lock (_lock)
        {
            foreach (var (channel, pin) in _pinMapping)
            {
                try
                {
                    _controller.Write(pin, PinValue.Low);
                    _channelStates[channel - 1] = 0;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex,
                        "Failed to turn off GPIO pin BCM{Pin} (channel {Channel})", pin, channel);
                    allOk = false;
                }
            }
        }

        _logger?.LogDebug("GPIO relay board gpiochip{ChipIndex}: all mapped pins set LOW", _chipIndex);
        return allOk;
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private void OpenMappedPins()
    {
        if (_controller == null)
            return;

        foreach (var (channel, pin) in _pinMapping)
        {
            try
            {
                if (!_controller.IsPinOpen(pin))
                    _controller.OpenPin(pin, PinMode.Output);

                // Restore cached state (default LOW)
                _controller.Write(pin, _channelStates[channel - 1] == 1 ? PinValue.High : PinValue.Low);

                _logger?.LogDebug("GPIO pin BCM{Pin} opened for channel {Channel}", pin, channel);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex,
                    "Failed to open GPIO pin BCM{Pin} for channel {Channel} on gpiochip{ChipIndex}",
                    pin, channel, _chipIndex);
            }
        }
    }

    private void CloseAllPins()
    {
        if (_controller == null)
            return;

        foreach (var (_, pin) in _pinMapping)
        {
            try
            {
                if (_controller.IsPinOpen(pin))
                    _controller.ClosePin(pin);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Error closing GPIO pin {Pin}", pin);
            }
        }
    }

    // ─── Dispose ─────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Close();
        _logger?.LogDebug("GPIO relay board gpiochip{ChipIndex} disposed", _chipIndex);
    }
}




