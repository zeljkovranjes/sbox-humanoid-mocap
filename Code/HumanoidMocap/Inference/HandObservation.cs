using System.Numerics;
namespace HumanoidMocap.Inference;
using Vector3 = System.Numerics.Vector3;
/// <param name="Handedness">The binary model probability for Side. Values below
/// 0.5 record disagreement when a separated spatial track retains its identity;
/// this is not a calibrated confidence in the 3D reconstruction.</param>
public sealed record HandObservation(string Side,float Presence,float Handedness,
    Vector3[] ImageLandmarks,Vector3[] RelativeWorldLandmarks,bool Tracked=false);
