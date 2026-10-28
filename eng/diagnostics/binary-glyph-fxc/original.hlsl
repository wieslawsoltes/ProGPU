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

struct Crossings {
    float2 x;
    int2 delta;
    float2 parameter;
};

struct Event {
    float x;
    int delta;
    uint source;
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
    uint lane_2 = 0u;
    float derivative = (float)0;

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
        float t_2 = ((level - _e4.y) / dy);
        if (((dy > 0.0) ? ((t_2 >= 0.0) && (t_2 < 1.0)) : ((t_2 > 0.0) && (t_2 <= 1.0)))) {
            result.x.x = (_e4.x + (t_2 * (_e6.x - _e4.x)));
            result.delta.x = ((dy > 0.0) ? 1 : -1);
            result.parameter.x = t_2;
        }
        Crossings _expr59 = result;
        const Crossings crossings_2 = _expr59;
        return crossings_2;
    }
    float a_3 = ((_e4.y - (2.0 * _e6.y)) + _e8.y);
    float b_3 = (2.0 * (_e6.y - _e4.y));
    float c_2 = (_e4.y - level);
    if ((a_3 == 0.0)) {
        if ((b_3 != 0.0)) {
            roots.x = (-(c_2) / b_3);
        }
    } else {
        float discriminant = ((b_3 * b_3) - ((4.0 * a_3) * c_2));
        if ((discriminant > 0.0)) {
            float d = sqrt(discriminant);
            float q = (-0.5 * (b_3 + ((b_3 >= 0.0) ? d : -(d))));
            roots = float2((q / a_3), (c_2 / q));
        } else {
            if ((discriminant == 0.0)) {
                roots.x = (-(b_3) / (2.0 * a_3));
            }
        }
    }
    bool loop_init = true;
    while(true) {
        if (!loop_init) {
            uint _expr171 = lane_2;
            lane_2 = (_expr171 + 1u);
        }
        loop_init = false;
        uint _expr111 = lane_2;
        if ((_expr111 < 2u)) {
        } else {
            break;
        }
        {
            uint _expr114 = lane_2;
            float t_3 = roots[_expr114];
            derivative = (((2.0 * a_3) * t_3) + b_3);
            float _expr122 = derivative;
            if (((_expr122 == 0.0) && (a_3 != 0.0))) {
                if ((t_3 == 0.0)) {
                    derivative = a_3;
                } else {
                    if ((t_3 == 1.0)) {
                        derivative = -(a_3);
                    }
                }
            }
            float _expr143 = derivative;
            bool admitted_1 = ((_expr143 > 0.0) ? ((t_3 >= 0.0) && (t_3 < 1.0)) : ((t_3 > 0.0) && (t_3 <= 1.0)));
            float _expr147 = derivative;
            if ((admitted_1 && (_expr147 != 0.0))) {
                uint _expr152 = lane_2;
                const float _e157 = quadratic_at(_e4.x, _e6.x, _e8.x, t_3);
                result.x[_expr152] = _e157;
                uint _expr159 = lane_2;
                float _expr163 = derivative;
                result.delta[_expr159] = ((_expr163 > 0.0) ? 1 : -1);
                uint _expr168 = lane_2;
                result.parameter[_expr168] = t_3;
            }
        }
    }
    Crossings _expr173 = result;
    const Crossings crossings_3 = _expr173;
    return crossings_3;
}

bool event_less(Event a_1, Event b_1)
{
    return ((a_1.x < b_1.x) || ((a_1.x == b_1.x) && (a_1.order < b_1.order)));
}

Event ConstructEvent(float arg0, int arg1, uint arg2, uint arg3) {
    Event ret = (Event)0;
    ret.x = arg0;
    ret.delta = arg1;
    ret.source = arg2;
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

void prepare_scan(GlyphRecord record, float level_1, bool vertical_2, uint lane_3)
{
    Crossings roots_1 = ConstructCrossings((3e38).xx, (0).xx, (-1.0).xx);
    uint size = 2u;
    uint distance_ = (uint)0;
    uint distance_1 = 1u;
    int low = (int)0;
    int high = (int)0;

    if ((lane_3 < record.segmentCount)) {
        Segment _expr18 = ConstructSegment(asfloat(segments.Load2((record.startSegment + lane_3)*48+0)), asfloat(segments.Load2((record.startSegment + lane_3)*48+8)), asfloat(segments.Load2((record.startSegment + lane_3)*48+16)), asfloat(segments.Load2((record.startSegment + lane_3)*48+24)), asuint(segments.Load((record.startSegment + lane_3)*48+32)), asuint(segments.Load((record.startSegment + lane_3)*48+36)), asuint(segments.Load((record.startSegment + lane_3)*48+40)), asuint(segments.Load((record.startSegment + lane_3)*48+44)));
        const Crossings _e19 = crossings(_expr18, level_1, vertical_2);
        roots_1 = _e19;
    }
    float _expr26 = roots_1.x.x;
    int _expr29 = roots_1.delta.x;
    events[(lane_3 * 2u)] = ConstructEvent(_expr26, _expr29, lane_3, (lane_3 * 2u));
    float _expr41 = roots_1.x.y;
    int _expr44 = roots_1.delta.y;
    events[((lane_3 * 2u) + 1u)] = ConstructEvent(_expr41, _expr44, lane_3, ((lane_3 * 2u) + 1u));
    GroupMemoryBarrierWithGroupSync();
    bool loop_init_1 = true;
    while(true) {
        if (!loop_init_1) {
            uint _expr94 = size;
            size = (_expr94 * 2u);
        }
        loop_init_1 = false;
        uint _expr52 = size;
        if ((_expr52 <= 512u)) {
        } else {
            break;
        }
        {
            uint _expr55 = size;
            distance_ = (_expr55 / 2u);
            bool loop_init_2 = true;
            while(true) {
                if (!loop_init_2) {
                    uint _expr91 = distance_;
                    distance_ = (_expr91 / 2u);
                }
                loop_init_2 = false;
                uint _expr59 = distance_;
                if ((_expr59 > 0u)) {
                } else {
                    break;
                }
                {
                    uint _expr62 = distance_;
                    uint _expr65 = distance_;
                    uint _expr68 = distance_;
                    uint left_2 = (((lane_3 / _expr62) * (2u * _expr65)) + (lane_3 % _expr68));
                    uint _expr71 = distance_;
                    uint right = (left_2 + _expr71);
                    Event a_4 = events[left_2];
                    Event b_4 = events[right];
                    uint _expr79 = size;
                    bool ascending = ((left_2 & _expr79) == 0u);
                    const bool _e83 = event_less(a_4, b_4);
                    const bool _e84 = event_less(b_4, a_4);
                    if ((ascending ? _e84 : _e83)) {
                        events[left_2] = b_4;
                        events[right] = a_4;
                    }
                    GroupMemoryBarrierWithGroupSync();
                }
            }
        }
    }
    int _expr101 = events[lane_3].delta;
    winding[lane_3] = _expr101;
    int _expr111 = events[(lane_3 + 256u)].delta;
    winding[(lane_3 + 256u)] = _expr111;
    GroupMemoryBarrierWithGroupSync();
    bool loop_init_3 = true;
    while(true) {
        if (!loop_init_3) {
            uint _expr158 = distance_1;
            distance_1 = (_expr158 * 2u);
        }
        loop_init_3 = false;
        uint _expr114 = distance_1;
        if ((_expr114 < 512u)) {
        } else {
            break;
        }
        {
            int _expr119 = winding[lane_3];
            low = _expr119;
            int _expr125 = winding[(lane_3 + 256u)];
            high = _expr125;
            uint _expr127 = distance_1;
            if ((lane_3 >= _expr127)) {
                uint _expr130 = distance_1;
                int _expr133 = winding[(lane_3 - _expr130)];
                int _expr134 = low;
                low = (_expr134 + _expr133);
            }
            uint _expr138 = distance_1;
            if (((lane_3 + 256u) >= _expr138)) {
                uint _expr143 = distance_1;
                int _expr146 = winding[((lane_3 + 256u) - _expr143)];
                int _expr147 = high;
                high = (_expr147 + _expr146);
            }
            GroupMemoryBarrierWithGroupSync();
            int _expr151 = low;
            winding[lane_3] = _expr151;
            int _expr156 = high;
            winding[(lane_3 + 256u)] = _expr156;
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

    bool loop_init_4 = true;
    while(true) {
        if (!loop_init_4) {
            uint _expr78 = i;
            i = (_expr78 + 1u);
        }
        loop_init_4 = false;
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
        uint _expr45 = events[_expr42].source;
        Segment edge_1 = ConstructSegment(asfloat(segments.Load2((record_3.startSegment + _expr45)*48+0)), asfloat(segments.Load2((record_3.startSegment + _expr45)*48+8)), asfloat(segments.Load2((record_3.startSegment + _expr45)*48+16)), asfloat(segments.Load2((record_3.startSegment + _expr45)*48+24)), asuint(segments.Load((record_3.startSegment + _expr45)*48+32)), asuint(segments.Load((record_3.startSegment + _expr45)*48+36)), asuint(segments.Load((record_3.startSegment + _expr45)*48+40)), asuint(segments.Load((record_3.startSegment + _expr45)*48+44)));
        float2 a_5 = (edge_1.p1_ - edge_1.p0_);
        float2 b_5 = (edge_1.p2_ - edge_1.p0_);
        return ((edge_1.segmentType == 1u) && ((a_5.x * b_5.y) != (a_5.y * b_5.x)));
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
    bool loop_init_5 = true;
    while(true) {
        if (!loop_init_5) {
            uint _expr131 = i_1;
            i_1 = (_expr131 + 1u);
        }
        loop_init_5 = false;
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
            bool loop_init_6 = true;
            while(true) {
                if (!loop_init_6) {
                    uint _expr128 = j;
                    j = (_expr128 + 1u);
                }
                loop_init_6 = false;
                uint _expr33 = j;
                if ((_expr33 < 2u)) {
                } else {
                    break;
                }
                {
                    uint _expr37 = j;
                    uint _expr41 = j;
                    uint _expr47 = j;
                    positions = Constructarray3_float2_(float2(_e24.x[_expr37], opposite), float2(left, _e26.x[_expr41]), float2((left + 1.0), _e30.x[_expr47]));
                    uint _expr53 = j;
                    uint _expr58 = j;
                    uint _expr63 = j;
                    uint _expr70 = j;
                    uint _expr75 = j;
                    uint _expr80 = j;
                    uint _expr85 = j;
                    uint _expr90 = j;
                    uint _expr95 = j;
                    admitted = Constructarray3_bool_((((_e24.delta[_expr53] != 0) && (_e24.x[_expr58] >= left)) && (_e24.x[_expr63] <= (left + 1.0))), (((_e26.delta[_expr70] != 0) && (_e26.x[_expr75] >= bottom)) && (_e26.x[_expr80] <= top)), (((_e30.delta[_expr85] != 0) && (_e30.x[_expr90] >= bottom)) && (_e30.x[_expr95] <= top)));
                    edge = 0u;
                    bool loop_init_7 = true;
                    while(true) {
                        if (!loop_init_7) {
                            uint _expr125 = edge;
                            edge = (_expr125 + 1u);
                        }
                        loop_init_7 = false;
                        uint _expr103 = edge;
                        if ((_expr103 < 3u)) {
                        } else {
                            break;
                        }
                        {
                            uint _expr106 = edge;
                            bool _expr108 = admitted[_expr106];
                            if (!(_expr108)) {
                                continue;
                            }
                            uint _expr110 = count;
                            if ((_expr110 == 0u)) {
                                uint _expr113 = edge;
                                float2 _expr115 = positions[_expr113];
                                first = _expr115;
                                count = 1u;
                            } else {
                                float2 _expr117 = first;
                                uint _expr118 = edge;
                                float2 _expr120 = positions[_expr118];
                                if (any((_expr117 != _expr120))) {
                                    return 2u;
                                }
                            }
                        }
                    }
                }
            }
        }
    }
    uint _expr133 = count;
    return _expr133;
}

float2 segment_end(Segment segment_1)
{
    return ((segment_1.segmentType == 0u) ? segment_1.p1_ : segment_1.p2_);
}

bool has_flat_cap(GlyphRecord record_6, Event beginning, Event finish, float left_1, float level_5, float direction_1, bool vertical_6)
{
    uint i_2 = 0u;

    Segment a_6 = ConstructSegment(asfloat(segments.Load2((record_6.startSegment + beginning.source)*48+0)), asfloat(segments.Load2((record_6.startSegment + beginning.source)*48+8)), asfloat(segments.Load2((record_6.startSegment + beginning.source)*48+16)), asfloat(segments.Load2((record_6.startSegment + beginning.source)*48+24)), asuint(segments.Load((record_6.startSegment + beginning.source)*48+32)), asuint(segments.Load((record_6.startSegment + beginning.source)*48+36)), asuint(segments.Load((record_6.startSegment + beginning.source)*48+40)), asuint(segments.Load((record_6.startSegment + beginning.source)*48+44)));
    Segment b_6 = ConstructSegment(asfloat(segments.Load2((record_6.startSegment + finish.source)*48+0)), asfloat(segments.Load2((record_6.startSegment + finish.source)*48+8)), asfloat(segments.Load2((record_6.startSegment + finish.source)*48+16)), asfloat(segments.Load2((record_6.startSegment + finish.source)*48+24)), asuint(segments.Load((record_6.startSegment + finish.source)*48+32)), asuint(segments.Load((record_6.startSegment + finish.source)*48+36)), asuint(segments.Load((record_6.startSegment + finish.source)*48+40)), asuint(segments.Load((record_6.startSegment + finish.source)*48+44)));
    if (((beginning.source == finish.source) || (a_6.contourStart != b_6.contourStart))) {
        return false;
    }
    const float2 _e35 = axis_point(a_6.p0_, vertical_6);
    const float2 _e38 = axis_point(a_6.p1_, vertical_6);
    const float2 _e43 = axis_point(b_6.p0_, vertical_6);
    const float2 _e46 = axis_point(b_6.p1_, vertical_6);
    bool orthogonal = ((((a_6.segmentType == 0u) && (b_6.segmentType == 0u)) && (_e35.x == _e38.x)) && (_e43.x == _e46.x));
    bool loop_init_8 = true;
    while(true) {
        if (!loop_init_8) {
            uint _expr137 = i_2;
            i_2 = (_expr137 + 1u);
        }
        loop_init_8 = false;
        uint _expr52 = i_2;
        if ((_expr52 < a_6.contourCount)) {
        } else {
            break;
        }
        {
            uint _expr59 = i_2;
            Segment cap = ConstructSegment(asfloat(segments.Load2(((record_6.startSegment + a_6.contourStart) + _expr59)*48+0)), asfloat(segments.Load2(((record_6.startSegment + a_6.contourStart) + _expr59)*48+8)), asfloat(segments.Load2(((record_6.startSegment + a_6.contourStart) + _expr59)*48+16)), asfloat(segments.Load2(((record_6.startSegment + a_6.contourStart) + _expr59)*48+24)), asuint(segments.Load(((record_6.startSegment + a_6.contourStart) + _expr59)*48+32)), asuint(segments.Load(((record_6.startSegment + a_6.contourStart) + _expr59)*48+36)), asuint(segments.Load(((record_6.startSegment + a_6.contourStart) + _expr59)*48+40)), asuint(segments.Load(((record_6.startSegment + a_6.contourStart) + _expr59)*48+44)));
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
            const float2 _e116 = segment_end(a_6);
            const float2 _e125 = segment_end(b_6);
            if (((all((_e116 == cap.p0_)) && all((b_6.p0_ == cap.p1_))) || (all((_e125 == cap.p0_)) && all((a_6.p0_ == cap.p1_))))) {
                return true;
            }
        }
    }
    return false;
}

void vertex_dropout(GlyphRecord record_7, uint index, float level_6, bool vertical_7)
{
    float2 before = (float2)0;
    float2 after = (float2)0;

    if ((index >= record_7.segmentCount)) {
        return;
    }
    Segment segment_4 = ConstructSegment(asfloat(segments.Load2((record_7.startSegment + index)*48+0)), asfloat(segments.Load2((record_7.startSegment + index)*48+8)), asfloat(segments.Load2((record_7.startSegment + index)*48+16)), asfloat(segments.Load2((record_7.startSegment + index)*48+24)), asuint(segments.Load((record_7.startSegment + index)*48+32)), asuint(segments.Load((record_7.startSegment + index)*48+36)), asuint(segments.Load((record_7.startSegment + index)*48+40)), asuint(segments.Load((record_7.startSegment + index)*48+44)));
    uint mode = (segment_4.scanDescriptor & 7u);
    if (((mode != 0u) && (mode != 4u))) {
        return;
    }
    uint previous_index = ((index == segment_4.contourStart) ? ((segment_4.contourStart + segment_4.contourCount) - 1u) : (index - 1u));
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

float smart_curve_crossing(GlyphRecord record_8, Event event, float level_7, bool vertical_8)
{
    float t_1 = (float)0;
    float2 a_2 = (float2)0;
    float2 b_2 = (float2)0;
    float2 c_1 = (float2)0;
    uint depth = 0u;

    Segment segment_5 = ConstructSegment(asfloat(segments.Load2((record_8.startSegment + event.source)*48+0)), asfloat(segments.Load2((record_8.startSegment + event.source)*48+8)), asfloat(segments.Load2((record_8.startSegment + event.source)*48+16)), asfloat(segments.Load2((record_8.startSegment + event.source)*48+24)), asuint(segments.Load((record_8.startSegment + event.source)*48+32)), asuint(segments.Load((record_8.startSegment + event.source)*48+36)), asuint(segments.Load((record_8.startSegment + event.source)*48+40)), asuint(segments.Load((record_8.startSegment + event.source)*48+44)));
    if ((segment_5.segmentType == 0u)) {
        return event.x;
    }
    const Crossings _e14 = crossings(segment_5, level_7, vertical_8);
    t_1 = _e14.parameter[(event.order % 2u)];
    const float2 _e22 = axis_point(segment_5.p0_, vertical_8);
    a_2 = _e22;
    const float2 _e25 = axis_point(segment_5.p1_, vertical_8);
    b_2 = _e25;
    const float2 _e28 = axis_point(segment_5.p2_, vertical_8);
    c_1 = _e28;
    if ((event.delta < 0)) {
        float2 swap = a_2;
        float2 _expr34 = c_1;
        a_2 = _expr34;
        c_1 = swap;
        float _expr35 = t_1;
        t_1 = (1.0 - _expr35);
    }
    bool loop_init_9 = true;
    while(true) {
        if (!loop_init_9) {
            uint _expr108 = depth;
            depth = (_expr108 + 1u);
        }
        loop_init_9 = false;
        uint _expr40 = depth;
        if ((_expr40 < 32u)) {
        } else {
            break;
        }
        {
            float _expr43 = t_1;
            if ((_expr43 == 0.0)) {
                float _expr47 = a_2.x;
                return _expr47;
            }
            float _expr48 = t_1;
            if ((_expr48 == 1.0)) {
                float _expr52 = c_1.x;
                return _expr52;
            }
            float _expr54 = a_2.x;
            float _expr56 = b_2.x;
            float _expr58 = c_1.x;
            float _expr62 = a_2.x;
            float _expr64 = b_2.x;
            float _expr66 = c_1.x;
            if (((max(_expr54, max(_expr56, _expr58)) - min(_expr62, min(_expr64, _expr66))) <= 0.015625)) {
                float _expr73 = a_2.x;
                return (floor((_expr73 * 64.0)) / 64.0);
            }
            float2 _expr79 = a_2;
            float2 _expr80 = b_2;
            float2 ab = ((_expr79 + _expr80) * 0.5);
            float2 _expr84 = b_2;
            float2 _expr85 = c_1;
            float2 bc = ((_expr84 + _expr85) * 0.5);
            float2 middle_1 = ((ab + bc) * 0.5);
            float _expr92 = t_1;
            if ((_expr92 == 0.5)) {
                return middle_1.x;
            }
            float _expr96 = t_1;
            if ((_expr96 < 0.5)) {
                b_2 = ab;
                c_1 = middle_1;
                float _expr100 = t_1;
                t_1 = (_expr100 * 2.0);
            } else {
                a_2 = middle_1;
                b_2 = bc;
                float _expr102 = t_1;
                t_1 = ((_expr102 - 0.5) * 2.0);
            }
        }
    }
    float _expr111 = a_2.x;
    return (floor((_expr111 * 64.0)) / 64.0);
}

void dropout(GlyphRecord record_9, uint index_1, float level_8, bool vertical_9)
{
    uint end = (uint)0;
    uint side = 0u;
    float selected = (float)0;
    float midpoint = (float)0;

    Event beginning_1 = events[index_1];
    int _expr12 = winding[index_1];
    if (((beginning_1.delta == 0) || (_expr12 == 0))) {
        return;
    }
    int _expr22 = winding[(index_1 - 1u)];
    if (((index_1 > 0u) && (_expr22 != 0))) {
        return;
    }
    uint _expr32 = asuint(segments.Load(36+(record_9.startSegment + beginning_1.source)*48));
    uint mode_1 = (_expr32 & 7u);
    if (((((mode_1 == 2u) || (mode_1 == 3u)) || (mode_1 == 6u)) || (mode_1 == 7u))) {
        return;
    }
    end = (index_1 + 1u);
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
        bool loop_init_10 = true;
        while(true) {
            if (!loop_init_10) {
                uint _expr148 = side;
                side = (_expr148 + 1u);
            }
            loop_init_10 = false;
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
        Segment first_segment = ConstructSegment(asfloat(segments.Load2((record_9.startSegment + beginning_1.source)*48+0)), asfloat(segments.Load2((record_9.startSegment + beginning_1.source)*48+8)), asfloat(segments.Load2((record_9.startSegment + beginning_1.source)*48+16)), asfloat(segments.Load2((record_9.startSegment + beginning_1.source)*48+24)), asuint(segments.Load((record_9.startSegment + beginning_1.source)*48+32)), asuint(segments.Load((record_9.startSegment + beginning_1.source)*48+36)), asuint(segments.Load((record_9.startSegment + beginning_1.source)*48+40)), asuint(segments.Load((record_9.startSegment + beginning_1.source)*48+44)));
        uint _expr165 = end;
        uint _expr168 = events[_expr165].source;
        Segment last_segment = ConstructSegment(asfloat(segments.Load2((record_9.startSegment + _expr168)*48+0)), asfloat(segments.Load2((record_9.startSegment + _expr168)*48+8)), asfloat(segments.Load2((record_9.startSegment + _expr168)*48+16)), asfloat(segments.Load2((record_9.startSegment + _expr168)*48+24)), asuint(segments.Load((record_9.startSegment + _expr168)*48+32)), asuint(segments.Load((record_9.startSegment + _expr168)*48+36)), asuint(segments.Load((record_9.startSegment + _expr168)*48+40)), asuint(segments.Load((record_9.startSegment + _expr168)*48+44)));
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
    if (all(__local_invocation_id == uint3(0u, 0u, 0u))) {
        events = (Event[512])0;
        winding = (int[512])0;
    }
    GroupMemoryBarrierWithGroupSync();
    uint word = (uint)0;
    uint x = (uint)0;

    uint row = group.y;
    uint _expr5 = uniforms.height;
    if ((row >= _expr5)) {
        return;
    }
    word = lane;
    bool loop_init_11 = true;
    while(true) {
        if (!loop_init_11) {
            uint _expr30 = word;
            word = (_expr30 + 256u);
        }
        loop_init_11 = false;
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
    bool loop_init_12 = true;
    while(true) {
        if (!loop_init_12) {
            uint _expr73 = x;
            x = (_expr73 + 256u);
        }
        loop_init_12 = false;
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
    dropout(record_10, lane, level_9, false);
    dropout(record_10, (lane + 256u), level_9, false);
    vertex_dropout(record_10, lane, level_9, false);
    return;
}

[numthreads(256, 1, 1)]
void cs_binary_vertical(uint3 group_1 : SV_GroupID, uint lane_1 : SV_GroupIndex, uint3 __local_invocation_id : SV_GroupThreadID)
{
    if (all(__local_invocation_id == uint3(0u, 0u, 0u))) {
        events = (Event[512])0;
        winding = (int[512])0;
    }
    GroupMemoryBarrierWithGroupSync();
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
    dropout(record_11, lane_1, level_10, true);
    dropout(record_11, (lane_1 + 256u), level_10, true);
    vertex_dropout(record_11, lane_1, level_10, true);
    return;
}
