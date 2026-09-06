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

// -- Sprite-local UV ------------------------------------------------------
//
// 2026-09-06 - EVERY TERM BELOW WAS READING THE WRONG UV ON AN ATLASED SPRITE,
// and the ones that mattered most were the ones that looked like they worked.
//
// input.uv on a packed sprite spans that sprite's SLOT on the atlas page, not
// 0..1. Assets/Art/Atlases/PitchAtlas packs pitch.png, ball.png, tile.png and
// backdrop.png - which is the pitch, the ball and every player body. So:
//
//   - the rim (`length(uv - 0.5)`) measured distance from the centre of the
//     PAGE, not of the body. For a slot sitting off to one side that expression
//     is nearly constant across the sprite, so the "rim light" on every player
//     was a flat tint. It looked deliberate, which is why it survived.
//   - the mown stripes ran at whatever fraction of _StripeCount the pitch's slot
//     width happened to subtend, so the lane count on screen was never the lane
//     count anybody set.
//
// Agent_Surfaces now measures the slot from Sprite.uv and writes it into
// _SpriteRect (offset.xy, size.zw), and every term maps through here first.
// The default is (0, 0, 1, 1) - an identity - so an unatlased sprite, the
// procedural FullRect quads and any material nobody configured all behave
// exactly as before.
float2 PoSoccerLocalUV(float2 uv)
{
    return (uv - _SpriteRect.xy) / max(_SpriteRect.zw, float2(1e-5, 1e-5));
}

// -- Kit patterns ---------------------------------------------------------
//
// A jersey is a SECOND COLOUR, not a brightness ripple, which is why this is
// separate from the stripe term above: _StripeStrength modulates the albedo it
// is given (right for mown grass, where the grass is the same grass either way)
// while a kit replaces it (right for a shirt, where the stripe is a different
// cloth).
//
// Modes, matching Reward_Settings.KitPattern:
//   0 none   1 stripes (vertical)   2 hoops (horizontal)   3 sash   4 halves
//
// All four are hard-edged with a one-texel smoothstep rather than antialiased
// by derivative, because a body is ~64 px on a portrait phone and ddx-based
// widths collapse to noise at that size.
half3 PoSoccerApplyKit(half3 albedo, float2 luv)
{
    if (_KitMode < 0.5h) return albedo;

    float mask;
    if (_KitMode < 1.5h)
    {
        // Stripes: vertical bands down the shirt.
        float wave = frac(luv.x * _KitScale);
        mask = step(0.5, wave);
    }
    else if (_KitMode < 2.5h)
    {
        // Hoops: horizontal bands.
        float wave = frac(luv.y * _KitScale);
        mask = step(0.5, wave);
    }
    else if (_KitMode < 3.5h)
    {
        // Sash: one diagonal band across the chest. Width is fixed rather than
        // scaled, so a sash reads as a sash at every _KitScale.
        float diagonal = frac((luv.x + luv.y) * 0.5);
        mask = 1.0 - smoothstep(0.20, 0.24, abs(diagonal - 0.5));
    }
    else
    {
        // Halves: left/right split.
        mask = step(0.5, luv.x);
    }

    return lerp(albedo, _KitColor.rgb, (half)mask * _KitColor.a);
}

half3 PoSoccerApplyFX(half3 albedo, float2 uv)
{
    float2 luv = PoSoccerLocalUV(uv);

    // -- Kit ---------------------------------------------------------------
    // First, so everything below shades the finished cloth rather than being
    // overwritten by it.
    albedo = PoSoccerApplyKit(albedo, luv);

    // -- Mown stripes ------------------------------------------------------
    // Alternating light and dark bands along an arbitrary axis, the way a roller
    // lays grass over. A soft square wave rather than sin() so the bands read as
    // mown lanes and not as a ripple.
    if (_StripeStrength > 0.0h)
    {
        float2 axis = float2(cos(_StripeAngle), sin(_StripeAngle));
        float projection = dot(luv - 0.5, axis);
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
        float along = frac(luv.x * 0.5 + luv.y * 0.5);
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
        float radial = saturate(length(luv - 0.5) * 2.0);
        float rim = pow(radial, _RimPower);
        albedo += _RimColor.rgb * rim * _RimStrength;
    }

    // -- Celebration flare -------------------------------------------------
    albedo *= 1.0h + _EmissionBoost;

    return albedo;
}

// Goal net.
//
// Returns an ALPHA multiplier rather than a colour, which is why it is a second
// function instead of another term inside PoSoccerApplyFX: a net is mostly hole.
// Modulating albedo can only darken a solid quad; the cords have to be cut out
// of the alpha or the goal reads as a painted board.
//
// Returns exactly 1.0 when _NetStrength is 0, so every other material in the
// project is bit-identical to before this existed.
half PoSoccerNetMask(float2 uv)
{
    if (_NetStrength <= 0.0h) return 1.0h;

    float2 centred = PoSoccerLocalUV(uv) - 0.5;
    float radius = length(centred);

    // The ripple is a radial UV displacement that decays with distance from the
    // strike point (taken as the centre of the mouth) and is driven entirely by
    // _NetRipple, which C# kicks to 1 on a goal and decays. At rest the term is
    // zero and the grid is perfectly still - a permanently rippling net reads as
    // wind, and this net is meant to read as impact.
    if (_NetRipple > 0.0h)
    {
        float wave = sin(radius * 34.0 - _Time.y * 26.0) * 0.035 * _NetRipple;
        centred += normalize(centred + 1e-5) * wave * exp(-radius * 2.5);
    }

    // Square grid of cords. fract-to-distance gives an evenly spaced lattice for
    // two ALU, and smoothstep keeps the cords from aliasing into moire when the
    // goal is small on a phone screen.
    float2 cell = abs(frac((centred + 0.5) * _NetTiling) - 0.5);
    float cords = 1.0 - smoothstep(0.0, 0.09, min(cell.x, cell.y));

    // Fade toward the rim so the net dissolves into the frame instead of ending
    // in a hard rectangular cut.
    float fade = 1.0 - smoothstep(0.32, 0.5, max(abs(centred.x), abs(centred.y)));

    return lerp(1.0h, (half)saturate(cords * fade), _NetStrength);
}

#endif // POSOCCER_FX_INCLUDED
