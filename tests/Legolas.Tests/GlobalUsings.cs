// Mirror the production-side global usings so tests reference the lifted types
// (#836) without a per-file `using Mithril.MapCalibration;`. See
// src/Legolas.Module/GlobalUsings.cs for the rationale.
global using AreaCalibration = Mithril.MapCalibration.AreaCalibration;
global using CalibrationSource = Mithril.MapCalibration.CalibrationSource;
global using LandmarkCalibrationSolver = Mithril.MapCalibration.LandmarkCalibrationSolver;
global using PixelPoint = Mithril.MapCalibration.PixelPoint;
global using WorldCoord = Mithril.MapCalibration.WorldCoord;
