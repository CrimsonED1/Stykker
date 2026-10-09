namespace Stykker.NanoCut.Geometry2D;

/// <summary>How the winding number of a point decides whether it is inside a set of contours.</summary>
public enum FillRule
{
    /// <summary>Inside if the winding number is odd.</summary>
    EvenOdd,

    /// <summary>Inside if the winding number is not zero.</summary>
    NonZero,

    /// <summary>Inside if the winding number is positive.</summary>
    Positive,

    /// <summary>Inside if the winding number is negative.</summary>
    Negative,
}

/// <summary>Boolean operation between a subject and a clip region.</summary>
public enum BooleanOp
{
    /// <summary>Points inside both.</summary>
    Intersection,

    /// <summary>Points inside either.</summary>
    Union,

    /// <summary>Points inside the subject but not the clip.</summary>
    Difference,

    /// <summary>Points inside exactly one of them.</summary>
    Xor,
}
