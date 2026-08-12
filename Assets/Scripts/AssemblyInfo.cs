using System.Runtime.CompilerServices;

// The observation-contract guard (Agent_EditMode_ObsContract) has to read the
// ray-sensor battery off Sensor_Vision to compute the expected model input size.
// Exposing that table publicly would put a test-only detail on the runtime API
// surface, which csharp-unity.md forbids without a named production caller, so
// the test assemblies get internals access instead.
[assembly: InternalsVisibleTo("PoSoccer.EditModeTests")]
[assembly: InternalsVisibleTo("PoSoccer.PlayModeTests")]
