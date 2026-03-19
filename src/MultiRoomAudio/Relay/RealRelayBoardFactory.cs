using MultiRoomAudio.Models.TriggerModels;
using MultiRoomAudio.Relay.Gpio;
using MultiRoomAudio.Relay.Hid;
using MultiRoomAudio.Relay.Modbus;

namespace MultiRoomAudio.Relay;

/// <summary>
/// Factory for creating real relay board instances (FTDI, HID, Modbus, LCUS, and GPIO).
/// </summary>
public class RealRelayBoardFactory : IRelayBoardFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RealRelayBoardFactory> _logger;

    public RealRelayBoardFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<RealRelayBoardFactory>();
    }

    /// <inheritdoc />
    public IRelayBoard CreateBoard(string boardId, RelayBoardType boardType)
    {
        _logger.LogDebug("Creating {BoardType} relay board for '{BoardId}'", boardType, boardId);

        return boardType switch
        {
            RelayBoardType.UsbHid => new HidRelayBoard(_loggerFactory.CreateLogger<HidRelayBoard>()),
            RelayBoardType.Ftdi => new FtdiRelayBoard(_loggerFactory.CreateLogger<FtdiRelayBoard>()),
            RelayBoardType.Modbus => CreateModbusBoard(boardId),
            RelayBoardType.Lcus => CreateLcusBoard(boardId),
            RelayBoardType.RaspberryPiGpio => CreateGpioBoard(boardId),
            _ => throw new ArgumentException($"Unsupported board type: {boardType}", nameof(boardType))
        };
    }

    private IRelayBoard CreateModbusBoard(string boardId)
    {
        int channelCount = 16;
        return new ModbusRelayBoard(_loggerFactory.CreateLogger<ModbusRelayBoard>(), channelCount);
    }

    private IRelayBoard CreateLcusBoard(string boardId)
    {
        int channelCount = 8;
        return new LcusRelayBoard(_loggerFactory.CreateLogger<LcusRelayBoard>(), channelCount);
    }

    /// <summary>
    /// Create a Raspberry Pi GPIO relay board from a board ID (e.g., "GPIO:gpiochip0").
    /// </summary>
    private IRelayBoard CreateGpioBoard(string boardId)
    {
        // Extract chip index: "GPIO:gpiochip0" → 0
        int chipIndex = 0;
        var chipPart = boardId.StartsWith("GPIO:", StringComparison.OrdinalIgnoreCase)
            ? boardId.Substring(5)
            : boardId;

        if (chipPart.StartsWith("gpiochip", StringComparison.OrdinalIgnoreCase))
            int.TryParse(chipPart.Substring("gpiochip".Length), out chipIndex);

        // Channel count will be overridden by board config after creation
        return new GpioRelayBoard(chipIndex, 8, _loggerFactory.CreateLogger<GpioRelayBoard>());
    }

    /// <inheritdoc />
    public bool CanCreate(string boardId, RelayBoardType boardType)
    {
        return boardType switch
        {
            RelayBoardType.UsbHid => true,
            RelayBoardType.Ftdi => FtdiRelayBoard.IsLibraryAvailable(),
            RelayBoardType.Modbus => true,
            RelayBoardType.Lcus => true,
            RelayBoardType.RaspberryPiGpio => GpioRelayBoard.IsAvailable(),
            RelayBoardType.Mock => false,
            _ => false
        };
    }
}
