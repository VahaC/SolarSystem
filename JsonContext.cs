using System.Text.Json.Serialization;

namespace SolarSystem;

/// <summary>
/// A11: AOT-friendly <c>System.Text.Json</c>. The roslyn source generator emits
/// a <c>JsonTypeInfo&lt;T&gt;</c> for every DTO listed below at compile time so
/// neither <see cref="System.Text.Json.JsonSerializer"/> nor the trimmer ever
/// have to fall back on runtime reflection.
///
/// Every call site in the project (<see cref="Bookmarks"/>, <see cref="CameraPath"/>,
/// <see cref="CometCatalog"/>, <see cref="Constellations"/>, <see cref="Localization"/>,
/// <see cref="Planet"/> and <see cref="SolarSystemWindow"/>'s persisted state)
/// goes through this context — see those files for the exact usage pattern.
///
/// Because every call site needs slightly different runtime options (case-
/// insensitive property matching, comment / trailing-comma tolerance,
/// pretty-printed writes, …) we don't bake any flags into
/// <see cref="JsonSourceGenerationOptionsAttribute"/>; consumers instead build
/// a <see cref="System.Text.Json.JsonSerializerOptions"/> on the fly and pass
/// it to a fresh <c>new SolarSystemJsonContext(opts)</c> when they need them.
/// </summary>
[JsonSerializable(typeof(Bookmarks.JsonEntry[]))]
[JsonSerializable(typeof(CameraPath.Waypoint))]
[JsonSerializable(typeof(CameraPath.Waypoint[]))]
[JsonSerializable(typeof(CometCatalog.CometsFile))]
[JsonSerializable(typeof(Constellations.ConstellationsFile))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Planet.PlanetsFile))]
[JsonSerializable(typeof(SolarSystemWindow.PersistedState))]
internal partial class SolarSystemJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
