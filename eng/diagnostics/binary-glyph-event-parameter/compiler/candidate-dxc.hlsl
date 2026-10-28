struct NagaConstants {
    int first_vertex;
    int first_instance;
    uint other;
};
ConstantBuffer<NagaConstants> _NagaConstants: register(b1);

struct GlyphUniforms {
    float xStart;
    float yStart;
    float scale;
    uint glyphIndex;
    uint outputOffsetWords;
    uint outputRowWords;
    uint width;
    uint height;
    float subpixelX;
    float atlasX;
    float atlasY;
    float _pad2_;
};

struct GlyphRecord {
    uint startSegment;
    uint segmentCount;
    float minX;
    float minY;
    float maxX;
    float maxY;
    uint _pad0_;
    uint _pad1_;
};

struct Segment {
    float2 p0_;
    float2 p1_;
    float2 p2_;
    float2 p3_;
    uint segmentType;
    uint scanDescriptor;
    uint contourStart;
    uint contourCount;
};

struct Crossing {
    float x;
    int delta;
    float parameter;
};

struct Crossings {
    float2 x;
    int2 delta;
    float2 parameter;
};

struct Event {
    float x;
    int delta;
    float parameter;
    uint order;
};

cbuffer uniforms : register(b0) { GlyphUniforms uniforms; }
ByteAddressBuffer glyphRecords : register(t0);
ByteAddressBuffer segments : register(t1);
RWByteAddressBuffer output : register(u0);
RWByteAddressBuffer horizontal : register(u1);
groupshared Event events[512];
groupshared int winding[512];

float2 axis_point(float2 p, bool vertical)
{
    return (vertical ? p.yx : p);
}

float quadratic_at(float a, float b, float c, float t)
{
    float u = (1.0 - t);
    return ((((u * u) * a) + (((2.0 * u) * t) * b)) + ((t * t) * c));
}

Crossing ConstructCrossing(float arg0, int arg1, float arg2) {
    Crossing ret = (Crossing)0;
    ret.x = arg0;
    ret.delta = arg1;
    ret.parameter = arg2;
    return ret;
}

Crossing quadratic_crossing(float2 p0_, float2 p1_, float2 p2_, float a_1, float b_1, float t_1)
{
    float derivative = (float)0;

    derivative = (((2.0 * a_1) * t_1) + b_1);
    float _expr11 = derivative;
    if (((_expr11 == 0.0) && (a_1 != 0.0))) {
        if ((t_1 == 0.0)) {
            derivative = a_1;
        } else {
            if ((t_1 == 1.0)) {
                derivative = -(a_1);
            }
        }
    }
    float _expr32 = derivative;
    bool admitted_1 = ((_expr32 > 0.0) ? ((t_1 >= 0.0) && (t_1 < 1.0)) : ((t_1 > 0.0) && (t_1 <= 1.0)));
    float _expr36 = derivative;
    if ((admitted_1 && (_expr36 != 0.0))) {
        const float _e43 = quadratic_at(p0_.x, p1_.x, p2_.x, t_1);
        float _expr46 = derivative;
        const Crossing crossing = ConstructCrossing(_e43, ((_expr46 > 0.0) ? 1 : -1), t_1);
        return crossing;
    }
    const Crossing crossing_1 = ConstructCrossing(3e38, 0, -1.0);
    return crossing_1;
}

Crossings ConstructCrossings(float2 arg0, int2 arg1, float2 arg2) {
    Crossings ret = (Crossings)0;
    ret.x = arg0;
    ret.delta = arg1;
    ret.parameter = arg2;
    return ret;
}

Crossings crossings(Segment segment, float level, bool vertical_1)
{
    Crossings result = ConstructCrossings((3e38).xx, (0).xx, (-1.0).xx);
    float2 roots = (-1.0).xx;

    const float2 _e4 = axis_point(segment.p0_, vertical_1);
    const float2 _e6 = axis_point(segment.p1_, vertical_1);
    const float2 _e8 = axis_point(segment.p2_, vertical_1);
    if ((segment.segmentType == 0u)) {
        float dy = (_e6.y - _e4.y);
        if ((dy == 0.0)) {
            Crossings _expr25 = result;
            const Crossings crossings_1 = _expr25;
            return crossings_1;
        }
        float t_3 = ((level - _e4.y) / dy);
        if (((dy > 0.0) ? ((t_3 >= 0.0) && (t_3 < 1.0)) : ((t_3 > 0.0) && (t_3 <= 1.0)))) {
            result.x.x = (_e4.x + (t_3 * (_e6.x - _e4.x)));
            result.delta.x = ((dy > 0.0) ? 1 : -1);
            result.parameter.x = t_3;
        }
        Crossings _expr59 = result;
        const Crossings crossings_2 = _expr59;
        return crossings_2;
    }
    float a_4 = ((_e4.y - (2.0 * _e6.y)) + _e8.y);
    float b_4 = (2.0 * (_e6.y - _e4.y));
    float c_2 = (_e4.y - level);
    if ((a_4 == 0.0)) {
        if ((b_4 != 0.0)) {
            roots.x = (-(c_2) / b_4);
        }
    } else {
        float discriminant = ((b_4 * b_4) - ((4.0 * a_4) * c_2));
        if ((discriminant > 0.0)) {
            float d = sqrt(discriminant);
            float q = (-0.5 * (b_4 + ((b_4 >= 0.0) ? d : -(d))));
            roots = float2((q / a_4), (c_2 / q));
        } else {
            if ((discriminant == 0.0)) {
                roots.x = (-(b_4) / (2.0 * a_4));
            }
        }
    }
    float _expr110 = roots.x;
    const Crossing _e111 = quadratic_crossing(_e4, _e6, _e8, a_4, b_4, _expr110);
    float _expr113 = roots.y;
    const Crossing _e114 = quadratic_crossing(_e4, _e6, _e8, a_4, b_4, _expr113);
    const Crossings crossings_3 = ConstructCrossings(float2(_e111.x, _e114.x), int2(_e111.delta, _e114.delta), float2(_e111.parameter, _e114.parameter));
    return crossings_3;
}

uint event_source(Event event)
{
    return (event.order / 2u);
}

bool event_less(Event a_2, Event b_2)
{
    return ((a_2.x < b_2.x) || ((a_2.x == b_2.x) && (a_2.order < b_2.order)));
}

Event ConstructEvent(float arg0, int arg1, float arg2, uint arg3) {
    Event ret = (Event)0;
    ret.x = arg0;
    ret.delta = arg1;
    ret.parameter = arg2;
    ret.order = arg3;
    return ret;
}

Segment ConstructSegment(float2 arg0, float2 arg1, float2 arg2, float2 arg3, uint arg4, uint arg5, uint arg6, uint arg7) {
    Segment ret = (Segment)0;
    ret.p0_ = arg0;
    ret.p1_ = arg1;
    ret.p2_ = arg2;
    ret.p3_ = arg3;
    ret.segmentType = arg4;
    ret.scanDescriptor = arg5;
    ret.contourStart = arg6;
    ret.contourCount = arg7;
    return ret;
}

void prepare_scan(GlyphRecord record, float level_1, bool vertical_2, uint lane_2)
{
    Crossings roots_1 = ConstructCrossings((3e38).xx, (0).xx, (-1.0).xx);
    uint size = 2u;
    uint distance_ = (uint)0;
    uint distance_1 = 1u;
    int low = (int)0;
    int high = (int)0;

    if ((lane_2 < record.segmentCount)) {
        Segment _expr18 = ConstructSegment(asfloat(segments.Load2((record.startSegment + lane_2)*48+0)), asfloat(segments.Load2((record.startSegment + lane_2)*48+8)), asfloat(segments.Load2((record.startSegment + lane_2)*48+16)), asfloat(segments.Load2((record.startSegment + lane_2)*48+24)), asuint(segments.Load((record.startSegment + lane_2)*48+32)), asuint(segments.Load((record.startSegment + lane_2)*48+36)), asuint(segments.Load((record.startSegment + lane_2)*48+40)), asuint(segments.Load((record.startSegment + lane_2)*48+44)));
        const Crossings _e19 = crossings(_expr18, level_1, vertical_2);
        roots_1 = _e19;
    }
    float _expr26 = roots_1.x.x;
    int _expr29 = roots_1.delta.x;
    float _expr32 = roots_1.parameter.x;
    events[(lane_2 * 2u)] = ConstructEvent(_expr26, _expr29, _expr32, (lane_2 * 2u));
    float _expr44 = roots_1.x.y;
    int _expr47 = roots_1.delta.y;
    float _expr50 = roots_1.parameter.y;
    events[((lane_2 * 2u) + 1u)] = ConstructEvent(_expr44, _expr47, _expr50, ((lane_2 * 2u) + 1u));
    GroupMemoryBarrierWithGroupSync();
    bool loop_init = true;
    while(true) {
        if (!loop_init) {
            uint _expr100 = size;
            size = (_expr100 * 2u);
        }
        loop_init = false;
        uint _expr58 = size;
        if ((_expr58 <= 512u)) {
        } else {
            break;
        }
        {
            uint _expr61 = size;
            distance_ = (_expr61 / 2u);
            bool loop_init_1 = true;
            while(true) {
                if (!loop_init_1) {
                    uint _expr97 = distance_;
                    distance_ = (_expr97 / 2u);
                }
                loop_init_1 = false;
                uint _expr65 = distance_;
                if ((_expr65 > 0u)) {
                } else {
                    break;
                }
                {
                    uint _expr68 = distance_;
                    uint _expr71 = distance_;
                    uint _expr74 = distance_;
                    uint left_2 = (((lane_2 / _expr68) * (2u * _expr71)) + (lane_2 % _expr74));
                    uint _expr77 = distance_;
                    uint right = (left_2 + _expr77);
                    Event a_5 = events[left_2];
                    Event b_5 = events[right];
                    uint _expr85 = size;
                    bool ascending = ((left_2 & _expr85) == 0u);
                    const bool _e89 = event_less(a_5, b_5);
                    const bool _e90 = event_less(b_5, a_5);
                    if ((ascending ? _e90 : _e89)) {
                        events[left_2] = b_5;
                        events[right] = a_5;
                    }
                    GroupMemoryBarrierWithGroupSync();
                }
            }
        }
    }
    int _expr107 = events[lane_2].delta;
    winding[lane_2] = _expr107;
    int _expr117 = events[(lane_2 + 256u)].delta;
    winding[(lane_2 + 256u)] = _expr117;
    GroupMemoryBarrierWithGroupSync();
    bool loop_init_2 = true;
    while(true) {
        if (!loop_init_2) {
            uint _expr164 = distance_1;
            distance_1 = (_expr164 * 2u);
        }
        loop_init_2 = false;
        uint _expr120 = distance_1;
        if ((_expr120 < 512u)) {
        } else {
            break;
        }
        {
            int _expr125 = winding[lane_2];
            low = _expr125;
            int _expr131 = winding[(lane_2 + 256u)];
            high = _expr131;
            uint _expr133 = distance_1;
            if ((lane_2 >= _expr133)) {
                uint _expr136 = distance_1;
                int _expr139 = winding[(lane_2 - _expr136)];
                int _expr140 = low;
                low = (_expr140 + _expr139);
            }
            uint _expr144 = distance_1;
            if (((lane_2 + 256u) >= _expr144)) {
                uint _expr149 = distance_1;
                int _expr152 = winding[((lane_2 + 256u) - _expr149)];
                int _expr153 = high;
                high = (_expr153 + _expr152);
            }
            GroupMemoryBarrierWithGroupSync();
            int _expr157 = low;
            winding[lane_2] = _expr157;
            int _expr162 = high;
            winding[(lane_2 + 256u)] = _expr162;
            GroupMemoryBarrierWithGroupSync();
        }
    }
    return;
}

float4 raster_bounds(GlyphRecord record_1)
{
    float2 low_3 = ceil((float2(record_1.minX, record_1.minY) - (0.5).xx));
    float2 high_3 = max((low_3 + (1.0).xx), floor((float2(record_1.maxX, record_1.maxY) + (0.5).xx)));
    return float4(low_3, high_3);
}

bool in_bounds(float2 point_, float4 bounds)
{
    return (all((point_ >= bounds.xy)) && all((point_ < bounds.zw)));
}

bool on_flat_boundary(GlyphRecord record_2, float x_1, float level_2, bool vertical_3)
{
    uint i = 0u;
    float low_1 = (float)0;
    float high_1 = (float)0;

    bool loop_init_3 = true;
    while(true) {
        if (!loop_init_3) {
            uint _expr78 = i;
            i = (_expr78 + 1u);
        }
        loop_init_3 = false;
        uint _expr6 = i;
        if ((_expr6 < record_2.segmentCount)) {
        } else {
            break;
        }
        {
            uint _expr11 = i;
            Segment segment_2 = ConstructSegment(asfloat(segments.Load2((record_2.startSegment + _expr11)*48+0)), asfloat(segments.Load2((record_2.startSegment + _expr11)*48+8)), asfloat(segments.Load2((record_2.startSegment + _expr11)*48+16)), asfloat(segments.Load2((record_2.startSegment + _expr11)*48+24)), asuint(segments.Load((record_2.startSegment + _expr11)*48+32)), asuint(segments.Load((record_2.startSegment + _expr11)*48+36)), asuint(segments.Load((record_2.startSegment + _expr11)*48+40)), asuint(segments.Load((record_2.startSegment + _expr11)*48+44)));
            const float2 _e16 = axis_point(segment_2.p0_, vertical_3);
            const float2 _e18 = axis_point(segment_2.p1_, vertical_3);
            const float2 _e20 = axis_point(segment_2.p2_, vertical_3);
            if (((_e16.y != level_2) || (_e18.y != level_2))) {
                continue;
            }
            low_1 = min(_e16.x, _e18.x);
            high_1 = max(_e16.x, _e18.x);
            if ((segment_2.segmentType != 0u)) {
                if ((_e20.y != level_2)) {
                    continue;
                }
                low_1 = min(_e16.x, _e20.x);
                high_1 = max(_e16.x, _e20.x);
                float denominator = ((_e16.x - (2.0 * _e18.x)) + _e20.x);
                if ((denominator != 0.0)) {
                    float t_4 = ((_e16.x - _e18.x) / denominator);
                    if (((t_4 > 0.0) && (t_4 < 1.0))) {
                        const float _e66 = quadratic_at(_e16.x, _e18.x, _e20.x, t_4);
                        float _expr67 = low_1;
                        low_1 = min(_expr67, _e66);
                        float _expr69 = high_1;
                        high_1 = max(_expr69, _e66);
                    }
                }
            }
            float _expr71 = low_1;
            float _expr73 = high_1;
            if (((x_1 >= _expr71) && (x_1 <= _expr73))) {
                return true;
            }
        }
    }
    return false;
}

bool base_covered(GlyphRecord record_3, float x_2, float level_3, bool vertical_4)
{
    uint low_2 = 0u;
    uint high_2 = 512u;

    const float2 _e5 = axis_point(float2(x_2, level_3), vertical_4);
    const float4 _e6 = raster_bounds(record_3);
    const bool _e7 = in_bounds(_e5, _e6);
    if (!(_e7)) {
        return false;
    }
    while(true) {
        uint _expr14 = low_2;
        uint _expr15 = high_2;
        if ((_expr14 < _expr15)) {
        } else {
            break;
        }
        {
            uint _expr17 = low_2;
            uint _expr18 = high_2;
            uint middle = ((_expr17 + _expr18) / 2u);
            float _expr25 = events[middle].x;
            if ((_expr25 < x_2)) {
                low_2 = (middle + 1u);
            } else {
                high_2 = middle;
            }
        }
    }
    uint _expr29 = low_2;
    uint _expr33 = low_2;
    float _expr36 = events[_expr33].x;
    if (((_expr29 < 512u) && (_expr36 == x_2))) {
        uint _expr42 = low_2;
        Event _expr44 = events[_expr42];
        const uint _e45 = event_source(_expr44);
        Segment edge_1 = ConstructSegment(asfloat(segments.Load2((record_3.startSegment + _e45)*48+0)), asfloat(segments.Load2((record_3.startSegment + _e45)*48+8)), asfloat(segments.Load2((record_3.startSegment + _e45)*48+16)), asfloat(segments.Load2((record_3.startSegment + _e45)*48+24)), asuint(segments.Load((record_3.startSegment + _e45)*48+32)), asuint(segments.Load((record_3.startSegment + _e45)*48+36)), asuint(segments.Load((record_3.startSegment + _e45)*48+40)), asuint(segments.Load((record_3.startSegment + _e45)*48+44)));
        float2 a_6 = (edge_1.p1_ - edge_1.p0_);
        float2 b_6 = (edge_1.p2_ - edge_1.p0_);
        return ((edge_1.segmentType == 1u) && ((a_6.x * b_6.y) != (a_6.y * b_6.x)));
    }
    uint _expr66 = low_2;
    uint _expr70 = low_2;
    int _expr74 = winding[(_expr70 - 1u)];
    const bool _e78 = on_flat_boundary(record_3, x_2, level_3, vertical_4);
    return (((_expr66 > 0u) && (_expr74 != 0)) && !(_e78));
}

uint2 pixel_address(float2 point_1)
{
    float _expr5 = uniforms.xStart;
    uint x_3 = uint((floor(point_1.x) - _expr5));
    float _expr15 = uniforms.yStart;
    uint y = uint(((-(floor(point_1.y)) - 1.0) - _expr15));
    uint _expr20 = uniforms.outputOffsetWords;
    uint _expr23 = uniforms.outputRowWords;
    return uint2(((_expr20 + (y * _expr23)) + (x_3 / 4u)), ((x_3 % 4u) * 8u));
}

void paint(float2 point_2, bool first_pass)
{
    const uint2 _e2 = pixel_address(point_2);
    uint bits = (255u << _e2.y);
    uint _e9; output.InterlockedOr(_e2.x*4, bits, _e9);
    if (first_pass) {
        uint _e13; horizontal.InterlockedOr(_e2.x*4, bits, _e13);
        return;
    } else {
        return;
    }
}

bool horizontal_covered(float2 point_3, GlyphRecord record_4)
{
    const float4 _e2 = raster_bounds(record_4);
    const bool _e3 = in_bounds(point_3, _e2);
    if (!(_e3)) {
        return false;
    }
    const uint2 _e6 = pixel_address(point_3);
    uint _expr10 = asuint(horizontal.Load(_e6.x*4));
    return (((_expr10 >> _e6.y) & 255u) != 0u);
}

typedef float2 ret_Constructarray3_float2_[3];
ret_Constructarray3_float2_ Constructarray3_float2_(float2 arg0, float2 arg1, float2 arg2) {
    float2 ret[3] = { arg0, arg1, arg2 };
    return ret;
}

typedef bool ret_Constructarray3_bool_[3];
ret_Constructarray3_bool_ Constructarray3_bool_(bool arg0, bool arg1, bool arg2) {
    bool ret[3] = { arg0, arg1, arg2 };
    return ret;
}

uint continuation_crossings(GlyphRecord record_5, float left, float level_4, float direction, bool vertical_5)
{
    uint count = 0u;
    float2 first = (0.0).xx;
    uint i_1 = 0u;
    uint j = (uint)0;
    float2 positions[3] = (float2[3])0;
    bool admitted[3] = (bool[3])0;
    uint edge = (uint)0;

    float opposite = (level_4 + direction);
    float bottom = min(level_4, opposite);
    float top = max(level_4, opposite);
    bool loop_init_4 = true;
    while(true) {
        if (!loop_init_4) {
            uint _expr143 = i_1;
            i_1 = (_expr143 + 1u);
        }
        loop_init_4 = false;
        uint _expr15 = i_1;
        if ((_expr15 < record_5.segmentCount)) {
        } else {
            break;
        }
        {
            uint _expr20 = i_1;
            Segment segment_3 = ConstructSegment(asfloat(segments.Load2((record_5.startSegment + _expr20)*48+0)), asfloat(segments.Load2((record_5.startSegment + _expr20)*48+8)), asfloat(segments.Load2((record_5.startSegment + _expr20)*48+16)), asfloat(segments.Load2((record_5.startSegment + _expr20)*48+24)), asuint(segments.Load((record_5.startSegment + _expr20)*48+32)), asuint(segments.Load((record_5.startSegment + _expr20)*48+36)), asuint(segments.Load((record_5.startSegment + _expr20)*48+40)), asuint(segments.Load((record_5.startSegment + _expr20)*48+44)));
            const Crossings _e24 = crossings(segment_3, opposite, vertical_5);
            const Crossings _e26 = crossings(segment_3, left, !(vertical_5));
            const Crossings _e30 = crossings(segment_3, (left + 1.0), !(vertical_5));
            j = 0u;
            bool loop_init_5 = true;
            while(true) {
                if (!loop_init_5) {
                    uint _expr140 = j;
                    j = (_expr140 + 1u);
                }
                loop_init_5 = false;
                uint _expr33 = j;
                if ((_expr33 < 2u)) {
                } else {
                    break;
                }
                {
                    uint _expr40 = j;
                    float across_x = ((_expr40 != 0u) ? _e24.x.y : _e24.x.x);
                    uint _expr48 = j;
                    int across_delta = ((_expr48 != 0u) ? _e24.delta.y : _e24.delta.x);
                    uint _expr56 = j;
                    float side0_x = ((_expr56 != 0u) ? _e26.x.y : _e26.x.x);
                    uint _expr64 = j;
                    int side0_delta = ((_expr64 != 0u) ? _e26.delta.y : _e26.delta.x);
                    uint _expr72 = j;
                    float side1_x = ((_expr72 != 0u) ? _e30.x.y : _e30.x.x);
                    uint _expr80 = j;
                    int side1_delta = ((_expr80 != 0u) ? _e30.delta.y : _e30.delta.x);
                    positions = Constructarray3_float2_(float2(across_x, opposite), float2(left, side0_x), float2((left + 1.0), side1_x));
                    admitted = Constructarray3_bool_((((across_delta != 0) && (across_x >= left)) && (across_x <= (left + 1.0))), (((side0_delta != 0) && (side0_x >= bottom)) && (side0_x <= top)), (((side1_delta != 0) && (side1_x >= bottom)) && (side1_x <= top)));
                    edge = 0u;
                    bool loop_init_6 = true;
                    while(true) {
                        if (!loop_init_6) {
                            uint _expr137 = edge;
                            edge = (_expr137 + 1u);
                        }
                        loop_init_6 = false;
                        uint _expr115 = edge;
                        if ((_expr115 < 3u)) {
                        } else {
                            break;
                        }
                        {
                            uint _expr118 = edge;
                            bool _expr120 = admitted[_expr118];
                            if (!(_expr120)) {
                                continue;
                            }
                            uint _expr122 = count;
                            if ((_expr122 == 0u)) {
                                uint _expr125 = edge;
                                float2 _expr127 = positions[_expr125];
                                first = _expr127;
                                count = 1u;
                            } else {
                                float2 _expr129 = first;
                                uint _expr130 = edge;
                                float2 _expr132 = positions[_expr130];
                                if (any((_expr129 != _expr132))) {
                                    return 2u;
                                }
                            }
                        }
                    }
                }
            }
        }
    }
    uint _expr145 = count;
    return _expr145;
}

float2 segment_end(Segment segment_1)
{
    return ((segment_1.segmentType == 0u) ? segment_1.p1_ : segment_1.p2_);
}

bool has_flat_cap(GlyphRecord record_6, Event beginning, Event finish, float left_1, float level_5, float direction_1, bool vertical_6)
{
    uint i_2 = 0u;

    const uint _e9 = event_source(beginning);
    Segment a_7 = ConstructSegment(asfloat(segments.Load2((record_6.startSegment + _e9)*48+0)), asfloat(segments.Load2((record_6.startSegment + _e9)*48+8)), asfloat(segments.Load2((record_6.startSegment + _e9)*48+16)), asfloat(segments.Load2((record_6.startSegment + _e9)*48+24)), asuint(segments.Load((record_6.startSegment + _e9)*48+32)), asuint(segments.Load((record_6.startSegment + _e9)*48+36)), asuint(segments.Load((record_6.startSegment + _e9)*48+40)), asuint(segments.Load((record_6.startSegment + _e9)*48+44)));
    const uint _e15 = event_source(finish);
    Segment b_7 = ConstructSegment(asfloat(segments.Load2((record_6.startSegment + _e15)*48+0)), asfloat(segments.Load2((record_6.startSegment + _e15)*48+8)), asfloat(segments.Load2((record_6.startSegment + _e15)*48+16)), asfloat(segments.Load2((record_6.startSegment + _e15)*48+24)), asuint(segments.Load((record_6.startSegment + _e15)*48+32)), asuint(segments.Load((record_6.startSegment + _e15)*48+36)), asuint(segments.Load((record_6.startSegment + _e15)*48+40)), asuint(segments.Load((record_6.startSegment + _e15)*48+44)));
    const uint _e19 = event_source(beginning);
    const uint _e20 = event_source(finish);
    if (((_e19 == _e20) || (a_7.contourStart != b_7.contourStart))) {
        return false;
    }
    const float2 _e35 = axis_point(a_7.p0_, vertical_6);
    const float2 _e38 = axis_point(a_7.p1_, vertical_6);
    const float2 _e43 = axis_point(b_7.p0_, vertical_6);
    const float2 _e46 = axis_point(b_7.p1_, vertical_6);
    bool orthogonal = ((((a_7.segmentType == 0u) && (b_7.segmentType == 0u)) && (_e35.x == _e38.x)) && (_e43.x == _e46.x));
    bool loop_init_7 = true;
    while(true) {
        if (!loop_init_7) {
            uint _expr137 = i_2;
            i_2 = (_expr137 + 1u);
        }
        loop_init_7 = false;
        uint _expr52 = i_2;
        if ((_expr52 < a_7.contourCount)) {
        } else {
            break;
        }
        {
            uint _expr59 = i_2;
            Segment cap = ConstructSegment(asfloat(segments.Load2(((record_6.startSegment + a_7.contourStart) + _expr59)*48+0)), asfloat(segments.Load2(((record_6.startSegment + a_7.contourStart) + _expr59)*48+8)), asfloat(segments.Load2(((record_6.startSegment + a_7.contourStart) + _expr59)*48+16)), asfloat(segments.Load2(((record_6.startSegment + a_7.contourStart) + _expr59)*48+24)), asuint(segments.Load(((record_6.startSegment + a_7.contourStart) + _expr59)*48+32)), asuint(segments.Load(((record_6.startSegment + a_7.contourStart) + _expr59)*48+36)), asuint(segments.Load(((record_6.startSegment + a_7.contourStart) + _expr59)*48+40)), asuint(segments.Load(((record_6.startSegment + a_7.contourStart) + _expr59)*48+44)));
            if ((cap.segmentType != 0u)) {
                continue;
            }
            const float2 _e67 = axis_point(cap.p0_, vertical_6);
            const float2 _e69 = axis_point(cap.p1_, vertical_6);
            float distance_2 = ((_e67.y - level_5) * direction_1);
            if (((((_e67.y != _e69.y) || (_e67.x == _e69.x)) || (distance_2 <= 0.0)) || (distance_2 > 1.0))) {
                continue;
            }
            bool boundary_contact = (((min(_e67.x, _e69.x) >= left_1) && (max(_e67.x, _e69.x) <= (left_1 + 1.0))) && ((((_e67.x == left_1) || (_e69.x == left_1)) || (_e67.x == (left_1 + 1.0))) || (_e69.x == (left_1 + 1.0))));
            if ((!(orthogonal) && !(boundary_contact))) {
                continue;
            }
            const float2 _e116 = segment_end(a_7);
            const float2 _e125 = segment_end(b_7);
            if (((all((_e116 == cap.p0_)) && all((b_7.p0_ == cap.p1_))) || (all((_e125 == cap.p0_)) && all((a_7.p0_ == cap.p1_))))) {
                return true;
            }
        }
    }
    return false;
}

void vertex_dropout(GlyphRecord record_7, uint index_2, float level_6, bool vertical_7)
{
    float2 before = (float2)0;
    float2 after = (float2)0;

    if ((index_2 >= record_7.segmentCount)) {
        return;
    }
    Segment segment_4 = ConstructSegment(asfloat(segments.Load2((record_7.startSegment + index_2)*48+0)), asfloat(segments.Load2((record_7.startSegment + index_2)*48+8)), asfloat(segments.Load2((record_7.startSegment + index_2)*48+16)), asfloat(segments.Load2((record_7.startSegment + index_2)*48+24)), asuint(segments.Load((record_7.startSegment + index_2)*48+32)), asuint(segments.Load((record_7.startSegment + index_2)*48+36)), asuint(segments.Load((record_7.startSegment + index_2)*48+40)), asuint(segments.Load((record_7.startSegment + index_2)*48+44)));
    uint mode = (segment_4.scanDescriptor & 7u);
    if (((mode != 0u) && (mode != 4u))) {
        return;
    }
    uint previous_index = ((index_2 == segment_4.contourStart) ? ((segment_4.contourStart + segment_4.contourCount) - 1u) : (index_2 - 1u));
    Segment previous = ConstructSegment(asfloat(segments.Load2((record_7.startSegment + previous_index)*48+0)), asfloat(segments.Load2((record_7.startSegment + previous_index)*48+8)), asfloat(segments.Load2((record_7.startSegment + previous_index)*48+16)), asfloat(segments.Load2((record_7.startSegment + previous_index)*48+24)), asuint(segments.Load((record_7.startSegment + previous_index)*48+32)), asuint(segments.Load((record_7.startSegment + previous_index)*48+36)), asuint(segments.Load((record_7.startSegment + previous_index)*48+40)), asuint(segments.Load((record_7.startSegment + previous_index)*48+44)));
    const float2 _e35 = axis_point(segment_4.p0_, vertical_7);
    const float2 _e37 = axis_point(previous.p0_, vertical_7);
    before = _e37;
    if ((previous.segmentType == 1u)) {
        const float2 _e43 = axis_point(previous.p1_, vertical_7);
        if ((_e43.y != _e35.y)) {
            before = _e43;
        }
    }
    const float2 _e48 = axis_point(segment_4.p1_, vertical_7);
    after = _e48;
    float _expr54 = after.y;
    if (((segment_4.segmentType == 1u) && (_expr54 == _e35.y))) {
        const float2 _e59 = axis_point(segment_4.p2_, vertical_7);
        after = _e59;
    }
    float _expr63 = before.y;
    float _expr66 = after.y;
    float _expr70 = before.y;
    float _expr73 = after.y;
    if (((_e35.y != level_6) || !((((_expr63 < level_6) && (_expr66 < level_6)) || ((_expr70 > level_6) && (_expr73 > level_6)))))) {
        return;
    }
    float left_3 = floor((_e35.x - 0.5));
    if ((_e35.x == (left_3 + 0.5))) {
        return;
    }
    const float2 _e90 = axis_point(float2((left_3 + 0.5), level_6), vertical_7);
    const float2 _e94 = axis_point(float2((left_3 + 1.5), level_6), vertical_7);
    if (vertical_7) {
        const bool _e95 = horizontal_covered(_e90, record_7);
        const bool _e96 = horizontal_covered(_e94, record_7);
        if ((_e95 || _e96)) {
            return;
        }
    } else {
        const bool _e101 = base_covered(record_7, (left_3 + 0.5), level_6, false);
        const bool _e105 = base_covered(record_7, (left_3 + 1.5), level_6, false);
        if ((_e101 || _e105)) {
            return;
        }
    }
    float selected_1 = (((mode == 4u) && (_e35.x > (left_3 + 1.0))) ? (left_3 + 1.0) : left_3);
    const float4 _e117 = raster_bounds(record_7);
    const float2 _e121 = axis_point(float2((selected_1 + 0.5), level_6), vertical_7);
    paint(clamp(_e121, (_e117.xy + (0.5).xx), (_e117.zw - (0.5).xx)), !(vertical_7));
    return;
}

float smart_curve_crossing(GlyphRecord record_8, Event event_1, float level_7, bool vertical_8)
{
    float t_2 = (float)0;
    float2 a_3 = (float2)0;
    float2 b_3 = (float2)0;
    float2 c_1 = (float2)0;
    uint depth = 0u;

    const uint _e6 = event_source(event_1);
    Segment segment_5 = ConstructSegment(asfloat(segments.Load2((record_8.startSegment + _e6)*48+0)), asfloat(segments.Load2((record_8.startSegment + _e6)*48+8)), asfloat(segments.Load2((record_8.startSegment + _e6)*48+16)), asfloat(segments.Load2((record_8.startSegment + _e6)*48+24)), asuint(segments.Load((record_8.startSegment + _e6)*48+32)), asuint(segments.Load((record_8.startSegment + _e6)*48+36)), asuint(segments.Load((record_8.startSegment + _e6)*48+40)), asuint(segments.Load((record_8.startSegment + _e6)*48+44)));
    if ((segment_5.segmentType == 0u)) {
        return event_1.x;
    }
    t_2 = event_1.parameter;
    const float2 _e17 = axis_point(segment_5.p0_, vertical_8);
    a_3 = _e17;
    const float2 _e20 = axis_point(segment_5.p1_, vertical_8);
    b_3 = _e20;
    const float2 _e23 = axis_point(segment_5.p2_, vertical_8);
    c_1 = _e23;
    if ((event_1.delta < 0)) {
        float2 swap = a_3;
        float2 _expr29 = c_1;
        a_3 = _expr29;
        c_1 = swap;
        float _expr30 = t_2;
        t_2 = (1.0 - _expr30);
    }
    bool loop_init_8 = true;
    while(true) {
        if (!loop_init_8) {
            uint _expr103 = depth;
            depth = (_expr103 + 1u);
        }
        loop_init_8 = false;
        uint _expr35 = depth;
        if ((_expr35 < 32u)) {
        } else {
            break;
        }
        {
            float _expr38 = t_2;
            if ((_expr38 == 0.0)) {
                float _expr42 = a_3.x;
                return _expr42;
            }
            float _expr43 = t_2;
            if ((_expr43 == 1.0)) {
                float _expr47 = c_1.x;
                return _expr47;
            }
            float _expr49 = a_3.x;
            float _expr51 = b_3.x;
            float _expr53 = c_1.x;
            float _expr57 = a_3.x;
            float _expr59 = b_3.x;
            float _expr61 = c_1.x;
            if (((max(_expr49, max(_expr51, _expr53)) - min(_expr57, min(_expr59, _expr61))) <= 0.015625)) {
                float _expr68 = a_3.x;
                return (floor((_expr68 * 64.0)) / 64.0);
            }
            float2 _expr74 = a_3;
            float2 _expr75 = b_3;
            float2 ab = ((_expr74 + _expr75) * 0.5);
            float2 _expr79 = b_3;
            float2 _expr80 = c_1;
            float2 bc = ((_expr79 + _expr80) * 0.5);
            float2 middle_1 = ((ab + bc) * 0.5);
            float _expr87 = t_2;
            if ((_expr87 == 0.5)) {
                return middle_1.x;
            }
            float _expr91 = t_2;
            if ((_expr91 < 0.5)) {
                b_3 = ab;
                c_1 = middle_1;
                float _expr95 = t_2;
                t_2 = (_expr95 * 2.0);
            } else {
                a_3 = middle_1;
                b_3 = bc;
                float _expr97 = t_2;
                t_2 = ((_expr97 - 0.5) * 2.0);
            }
        }
    }
    float _expr106 = a_3.x;
    return (floor((_expr106 * 64.0)) / 64.0);
}

void dropout(GlyphRecord record_9, uint index_3, float level_8, bool vertical_9)
{
    uint end = (uint)0;
    uint side = 0u;
    float selected = (float)0;
    float midpoint = (float)0;

    Event beginning_1 = events[index_3];
    int _expr12 = winding[index_3];
    if (((beginning_1.delta == 0) || (_expr12 == 0))) {
        return;
    }
    int _expr22 = winding[(index_3 - 1u)];
    if (((index_3 > 0u) && (_expr22 != 0))) {
        return;
    }
    const uint _e28 = event_source(beginning_1);
    uint _expr32 = asuint(segments.Load(36+(record_9.startSegment + _e28)*48));
    uint mode_1 = (_expr32 & 7u);
    if (((((mode_1 == 2u) || (mode_1 == 3u)) || (mode_1 == 6u)) || (mode_1 == 7u))) {
        return;
    }
    end = (index_3 + 1u);
    while(true) {
        uint _expr49 = end;
        uint _expr53 = end;
        int _expr55 = winding[_expr53];
        if (((_expr49 < 512u) && (_expr55 != 0))) {
        } else {
            break;
        }
        {
            uint _expr60 = end;
            end = (_expr60 + 1u);
        }
    }
    uint _expr62 = end;
    if ((_expr62 == 512u)) {
        return;
    }
    uint _expr66 = end;
    float finish_1 = events[_expr66].x;
    float left_4 = floor((beginning_1.x - 0.5));
    float right_1 = (left_4 + 1.0);
    bool gap = (vertical_9 ? (((left_4 + 0.5) <= beginning_1.x) && (finish_1 < (right_1 + 0.5))) : (((left_4 + 0.5) < beginning_1.x) && (finish_1 <= (right_1 + 0.5))));
    if (!((gap && (beginning_1.x < finish_1)))) {
        return;
    }
    const float2 _e100 = axis_point(float2((left_4 + 0.5), level_8), vertical_9);
    const float2 _e104 = axis_point(float2((right_1 + 0.5), level_8), vertical_9);
    if (vertical_9) {
        const bool _e105 = horizontal_covered(_e100, record_9);
        const bool _e106 = horizontal_covered(_e104, record_9);
        if ((_e105 || _e106)) {
            return;
        }
    } else {
        const bool _e111 = base_covered(record_9, (left_4 + 0.5), level_8, false);
        const bool _e115 = base_covered(record_9, (right_1 + 0.5), level_8, false);
        if ((_e111 || _e115)) {
            return;
        }
    }
    if (((mode_1 == 1u) || (mode_1 == 5u))) {
        bool loop_init_9 = true;
        while(true) {
            if (!loop_init_9) {
                uint _expr148 = side;
                side = (_expr148 + 1u);
            }
            loop_init_9 = false;
            uint _expr124 = side;
            if ((_expr124 < 2u)) {
            } else {
                break;
            }
            {
                uint _expr129 = side;
                float direction_2 = ((_expr129 != 0u) ? 1.0 : -1.0);
                const uint _e135 = continuation_crossings(record_9, (left_4 + 0.5), level_8, direction_2, vertical_9);
                uint _expr139 = end;
                Event _expr141 = events[_expr139];
                const bool _e144 = has_flat_cap(record_9, beginning_1, _expr141, (left_4 + 0.5), level_8, direction_2, vertical_9);
                if (((_e135 < 2u) && !(_e144))) {
                    return;
                }
            }
        }
    }
    selected = left_4;
    if (((mode_1 == 4u) || (mode_1 == 5u))) {
        const uint _e158 = event_source(beginning_1);
        Segment first_segment = ConstructSegment(asfloat(segments.Load2((record_9.startSegment + _e158)*48+0)), asfloat(segments.Load2((record_9.startSegment + _e158)*48+8)), asfloat(segments.Load2((record_9.startSegment + _e158)*48+16)), asfloat(segments.Load2((record_9.startSegment + _e158)*48+24)), asuint(segments.Load((record_9.startSegment + _e158)*48+32)), asuint(segments.Load((record_9.startSegment + _e158)*48+36)), asuint(segments.Load((record_9.startSegment + _e158)*48+40)), asuint(segments.Load((record_9.startSegment + _e158)*48+44)));
        uint _expr165 = end;
        Event _expr167 = events[_expr165];
        const uint _e168 = event_source(_expr167);
        Segment last_segment = ConstructSegment(asfloat(segments.Load2((record_9.startSegment + _e168)*48+0)), asfloat(segments.Load2((record_9.startSegment + _e168)*48+8)), asfloat(segments.Load2((record_9.startSegment + _e168)*48+16)), asfloat(segments.Load2((record_9.startSegment + _e168)*48+24)), asuint(segments.Load((record_9.startSegment + _e168)*48+32)), asuint(segments.Load((record_9.startSegment + _e168)*48+36)), asuint(segments.Load((record_9.startSegment + _e168)*48+40)), asuint(segments.Load((record_9.startSegment + _e168)*48+44)));
        midpoint = ((beginning_1.x + finish_1) * 0.5);
        if (((first_segment.segmentType == 1u) || (last_segment.segmentType == 1u))) {
            const float _e184 = smart_curve_crossing(record_9, beginning_1, level_8, vertical_9);
            uint _expr186 = end;
            Event _expr188 = events[_expr186];
            const float _e189 = smart_curve_crossing(record_9, _expr188, level_8, vertical_9);
            midpoint = (ceil((((_e184 + _e189) * 32.0) - 0.5)) / 64.0);
        }
        float _expr198 = midpoint;
        if ((_expr198 > right_1)) {
            selected = right_1;
        }
    }
    const float4 _e200 = raster_bounds(record_9);
    float _expr201 = selected;
    const float2 _e205 = axis_point(float2((_expr201 + 0.5), level_8), vertical_9);
    paint(clamp(_e205, (_e200.xy + (0.5).xx), (_e200.zw - (0.5).xx)), !(vertical_9));
    return;
}

GlyphRecord ConstructGlyphRecord(uint arg0, uint arg1, float arg2, float arg3, float arg4, float arg5, uint arg6, uint arg7) {
    GlyphRecord ret = (GlyphRecord)0;
    ret.startSegment = arg0;
    ret.segmentCount = arg1;
    ret.minX = arg2;
    ret.minY = arg3;
    ret.maxX = arg4;
    ret.maxY = arg5;
    ret._pad0_ = arg6;
    ret._pad1_ = arg7;
    return ret;
}

[numthreads(256, 1, 1)]
void cs_binary_horizontal(uint3 group : SV_GroupID, uint lane : SV_GroupIndex, uint3 __local_invocation_id : SV_GroupThreadID)
{
    uint workgroup_zero_local_index = __local_invocation_id.x + 256u * (__local_invocation_id.y + 1u * __local_invocation_id.z);
    for (uint workgroup_zero_index = workgroup_zero_local_index; workgroup_zero_index < 512u; ) {
        events[workgroup_zero_index] = (Event)0;
        workgroup_zero_index += 256u;
    }
    for (uint workgroup_zero_index_1 = workgroup_zero_local_index; workgroup_zero_index_1 < 512u; ) {
        winding[workgroup_zero_index_1] = (int)0;
        workgroup_zero_index_1 += 256u;
    }
    GroupMemoryBarrierWithGroupSync();
    uint word = (uint)0;
    uint x = (uint)0;
    uint index = (uint)0;

    uint row = group.y;
    uint _expr5 = uniforms.height;
    if ((row >= _expr5)) {
        return;
    }
    word = lane;
    bool loop_init_10 = true;
    while(true) {
        if (!loop_init_10) {
            uint _expr30 = word;
            word = (_expr30 + 256u);
        }
        loop_init_10 = false;
        uint _expr8 = word;
        uint _expr11 = uniforms.outputRowWords;
        if ((_expr8 < _expr11)) {
        } else {
            break;
        }
        {
            uint _expr15 = uniforms.outputOffsetWords;
            uint _expr18 = uniforms.outputRowWords;
            uint _expr21 = word;
            uint address = ((_expr15 + (row * _expr18)) + _expr21);
            output.Store(address*4, asuint(0u));
            horizontal.Store(address*4, asuint(0u));
        }
    }
    DeviceMemoryBarrierWithGroupSync();
    uint _expr35 = uniforms.glyphIndex;
    GlyphRecord record_10 = ConstructGlyphRecord(asuint(glyphRecords.Load(_expr35*32+0)), asuint(glyphRecords.Load(_expr35*32+4)), asfloat(glyphRecords.Load(_expr35*32+8)), asfloat(glyphRecords.Load(_expr35*32+12)), asfloat(glyphRecords.Load(_expr35*32+16)), asfloat(glyphRecords.Load(_expr35*32+20)), asuint(glyphRecords.Load(_expr35*32+24)), asuint(glyphRecords.Load(_expr35*32+28)));
    float _expr40 = uniforms.yStart;
    float level_9 = -(((_expr40 + float(row)) + 0.5));
    const float4 _e46 = raster_bounds(record_10);
    prepare_scan(record_10, level_9, false, lane);
    if (((level_9 < _e46.y) || (level_9 >= _e46.w))) {
        return;
    }
    x = lane;
    bool loop_init_11 = true;
    while(true) {
        if (!loop_init_11) {
            uint _expr73 = x;
            x = (_expr73 + 256u);
        }
        loop_init_11 = false;
        uint _expr54 = x;
        uint _expr57 = uniforms.width;
        if ((_expr54 < _expr57)) {
        } else {
            break;
        }
        {
            float _expr61 = uniforms.xStart;
            uint _expr62 = x;
            float2 point_4 = float2(((_expr61 + float(_expr62)) + 0.5), level_9);
            const bool _e70 = base_covered(record_10, point_4.x, level_9, false);
            if (_e70) {
                paint(point_4, true);
            }
        }
    }
    index = lane;
    bool loop_init_12 = true;
    while(true) {
        if (!loop_init_12) {
            uint _expr82 = index;
            index = (_expr82 + 256u);
        }
        loop_init_12 = false;
        uint _expr76 = index;
        if ((_expr76 < 512u)) {
        } else {
            break;
        }
        {
            uint _expr79 = index;
            dropout(record_10, _expr79, level_9, false);
        }
    }
    vertex_dropout(record_10, lane, level_9, false);
    return;
}

[numthreads(256, 1, 1)]
void cs_binary_vertical(uint3 group_1 : SV_GroupID, uint lane_1 : SV_GroupIndex, uint3 __local_invocation_id : SV_GroupThreadID)
{
    uint workgroup_zero_local_index_1 = __local_invocation_id.x + 256u * (__local_invocation_id.y + 1u * __local_invocation_id.z);
    for (uint workgroup_zero_index_2 = workgroup_zero_local_index_1; workgroup_zero_index_2 < 512u; ) {
        events[workgroup_zero_index_2] = (Event)0;
        workgroup_zero_index_2 += 256u;
    }
    for (uint workgroup_zero_index_3 = workgroup_zero_local_index_1; workgroup_zero_index_3 < 512u; ) {
        winding[workgroup_zero_index_3] = (int)0;
        workgroup_zero_index_3 += 256u;
    }
    GroupMemoryBarrierWithGroupSync();
    uint index_1 = (uint)0;

    uint column = group_1.y;
    uint _expr5 = uniforms.width;
    if ((column >= _expr5)) {
        return;
    }
    uint _expr10 = uniforms.glyphIndex;
    GlyphRecord record_11 = ConstructGlyphRecord(asuint(glyphRecords.Load(_expr10*32+0)), asuint(glyphRecords.Load(_expr10*32+4)), asfloat(glyphRecords.Load(_expr10*32+8)), asfloat(glyphRecords.Load(_expr10*32+12)), asfloat(glyphRecords.Load(_expr10*32+16)), asfloat(glyphRecords.Load(_expr10*32+20)), asuint(glyphRecords.Load(_expr10*32+24)), asuint(glyphRecords.Load(_expr10*32+28)));
    float _expr15 = uniforms.xStart;
    float level_10 = ((_expr15 + float(column)) + 0.5);
    const float4 _e20 = raster_bounds(record_11);
    prepare_scan(record_11, level_10, true, lane_1);
    if (((level_10 < _e20.x) || (level_10 >= _e20.z))) {
        return;
    }
    index_1 = lane_1;
    bool loop_init_13 = true;
    while(true) {
        if (!loop_init_13) {
            uint _expr34 = index_1;
            index_1 = (_expr34 + 256u);
        }
        loop_init_13 = false;
        uint _expr28 = index_1;
        if ((_expr28 < 512u)) {
        } else {
            break;
        }
        {
            uint _expr31 = index_1;
            dropout(record_11, _expr31, level_10, true);
        }
    }
    vertex_dropout(record_11, lane_1, level_10, true);
    return;
}
