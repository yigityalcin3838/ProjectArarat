// Where on a body a hit landed.
//
// Deliberately anatomical rather than mechanical -- not "weak point" or "armour
// zone" -- because the same list has to answer several different questions later
// and none of them is only about damage: which piece recoils when struck, which
// piece can be knocked off and left on the ground, and which foot is being planted
// on a slope. A part named for what it IS survives all three; a part named for what
// it currently does would have to be renamed by the first feature that disagreed.
//
// Shoulders are listed apart from upper arms even though their colliders will touch,
// because the shoulder is the piece that visibly takes a hit and swings back while
// the arm hanging off it follows. They are one region to look at and two things to
// move.
public enum EnemyBodyPart
{
    Head,
    Torso,
    Pelvis,

    LeftShoulder,
    LeftUpperArm,
    LeftLowerArm,
    LeftHand,

    RightShoulder,
    RightUpperArm,
    RightLowerArm,
    RightHand,

    LeftThigh,
    LeftCalf,
    LeftFoot,

    RightThigh,
    RightCalf,
    RightFoot,
}
