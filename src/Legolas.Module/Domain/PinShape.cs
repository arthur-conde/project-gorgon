using System.Text.Json.Serialization;

namespace Legolas.Domain;

/// <summary>
/// Shape primitive used by the survey-pin DataTemplate's outer + centre Paths.
/// <see cref="None"/> hides the corresponding shape entirely (renders as
/// <c>Geometry.Empty</c>) — useful for users who want only the outer ring or
/// only the centre marker.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<PinShape>))]
public enum PinShape
{
    Circle,
    Square,
    Diamond,
    Cross,
    None,
}

/// <summary>Stroke pattern for pin outlines.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PinStrokeStyle>))]
public enum PinStrokeStyle
{
    None,
    Solid,
    Dashed,
}

/// <summary>
/// Visual treatment applied to the pin currently held in
/// <c>SessionState.SelectedSurvey</c> while the FSM is in <c>Listening</c>.
/// Only <see cref="Halo"/> is wired in the initial pass; the others are
/// reserved enum slots so a follow-up PR can add them without a schema bump.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ActivePinTreatment>))]
public enum ActivePinTreatment
{
    Halo,
    Glow,
    ScaleUp,
    FillSwap,
}
