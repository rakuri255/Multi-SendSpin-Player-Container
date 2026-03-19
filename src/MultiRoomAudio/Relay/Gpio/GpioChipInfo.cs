namespace MultiRoomAudio.Relay.Gpio;

/// <summary>
/// Information about a discovered GPIO chip device.
/// </summary>
public record GpioChipInfo(
    int ChipIndex,
    string DevicePath,
    string Description
)
{
    /// <summary>
    /// Board ID used to identify this chip in the relay system.
    /// Format: "GPIO:gpiochipN"
    /// </summary>
    public string GetBoardId() => $"GPIO:gpiochip{ChipIndex}";
}

