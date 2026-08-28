#ifndef POSOCCER_FX_INCLUDED
#define POSOCCER_FX_INCLUDED

// Shared albedo modulation for PoSoccer/SpriteLitFX.
//
// Applied to the sampled albedo BEFORE the 2D lighting pass, so stripes, sheen
// and rims are shaded by the stadium floodlights instead of sitting flat on top
// of the lit result. Every term multiplies by a strength that defaults to zero,
// so an unconfigured material is identical to Sprite-Lit-Default.
//
// Assumes the CBUFFER declaring these properties is already in scope.

half3 PoSoccerApplyFX(half3 albedo, float2 uv)
{
    // -- Mown stripes ------------------------------------------------------
    // Alternating light and dark bands along an arbitrary axis, the way a roller
    // lays grass over. A soft square wave rather than sin() so the bands read as
    // mown lanes and not as a ripple.
    if (_StripeStrength > 0.0h)
    {
        float2 axis = float2(cos(_StripeAngle), sin(_StripeAngle));
        float projection = dot(uv - 0.5, axis);
        float wave = frac(projection * _StripeCount);
        // smoothstep pair = a band with soft shoulders, cheap and alias-free.
        float band = smoothstep(0.0, 0.08, wave) - smoothstep(0.5, 0.58, wave);
        albedo *= 1.0h + _StripeStrength * (band * 2.0h - 1.0h);
    }

    // -- Travelling sheen --------------------------------------------------
    // A highlight band sweeping across the surface: wet grass under floodlights,
    // and the scrolling gloss on the advertising boards.
    if (_SheenStrength > 0.0h)
    {
        float travel = frac(_Time.y * _SheenSpeed);
        float along = frac(uv.x * 0.5 + uv.y * 0.5);
        float distance = abs(along - travel);
        distance = min(distance, 1.0 - distance);          // wrap, so it loops seamlessly
        float band = exp(-distance * distance * _SheenWidth * _SheenWidth);
        albedo += _SheenStrength * band;
    }

    // -- Team rim ----------------------------------------------------------
    // Radial falloff from the sprite centre. On a round body this reads as a rim
    // light and replaces the four LineRenderers previously drawn per player.
    if (_RimStrength > 0.0h)
    {
        float radial = saturate(length(uv - 0.5) * 2.0);
        float rim = pow(radial, _RimPower);
        albedo += _RimColor.rgb * rim * _RimStrength;
    }

    // -- Celebration flare -------------------------------------------------
    albedo *= 1.0h + _EmissionBoost;

    return albedo;
}

#endif // POSOCCER_FX_INCLUDED
