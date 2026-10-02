#!/usr/bin/env python3
"""Independent analytic polynomial derivation; no SDK input or product execution.

Remez exchange follows the equioscillation equations in DLMF 3.11. Coefficients
come exclusively from analytic Taylor series evaluated with Decimal arithmetic.
This is numerical analysis, not a proof of SDK coefficients or source admission.
"""

import argparse
from decimal import Decimal as D, getcontext
import json
import struct

getcontext().prec = 70
ZERO, ONE = D(0), D(1)
EPS = D("1e-62")


def arctan_small(x):
    term, total, square = x, x, x * x
    for n in range(1, 256):
        term *= -square
        add = term / D(2 * n + 1)
        total += add
        if abs(add) < EPS:
            return total
    raise ArithmeticError("arctangent series budget")


PI = 16 * arctan_small(ONE / 5) - 4 * arctan_small(ONE / 239)


def analytic_sincos(x):
    sine = sine_term = x
    cosine = cosine_term = ONE
    square = x * x
    for n in range(1, 100):
        sine_term *= -square / D((2 * n) * (2 * n + 1))
        cosine_term *= -square / D((2 * n - 1) * (2 * n))
        sine += sine_term
        cosine += cosine_term
        if max(abs(sine_term), abs(cosine_term)) < EPS:
            return sine, cosine
    raise ArithmeticError("trigonometric series budget")


def target(kind, x):
    if kind == "cosroot":
        # Remove the known endpoint root analytically. The limit and derivative
        # follow twice differentiating numerator/denominator (not SDK data).
        endpoint = PI / 2
        if x == endpoint:
            return endpoint / 2, -ONE / 4
        sine, cosine = analytic_sincos(x)
        denominator = 1 - x * x / (endpoint * endpoint)
        slope = -2 * x / (endpoint * endpoint)
        return cosine / denominator, (-sine * denominator - cosine * slope) / (denominator * denominator)
    if kind == "cosc":
        # Entire even function (cos(x)-1)/x^2, evaluated without cancellation
        # at zero. Its degree-eight approximation reconstructs degree-ten cos.
        term, value, derivative = -ONE / 2, -ONE / 2, ZERO
        for n in range(1, 100):
            coefficient = (-ONE) ** (n + 1)
            factorial = ONE
            for factor in range(2, 2 * n + 3):
                factorial *= factor
            coefficient /= factorial
            term = coefficient * x ** (2 * n)
            value += term
            derivative += (2 * n) * coefficient * x ** (2 * n - 1)
            if n > 2 and abs(term) < EPS:
                return value, derivative
        raise ArithmeticError("normalized cosine series budget")
    sine, cosine = analytic_sincos(x)
    if kind == "sin":
        return sine, cosine
    if kind == "cos":
        return cosine, -sine
    if x == 0:
        return ONE, ZERO
    return sine / x, (cosine * x - sine) / (x * x)


def polynomial(coefficients, x):
    value, derivative = ZERO, ZERO
    for power, coefficient in coefficients.items():
        value += coefficient * (x ** power if power else ONE)
        if power:
            derivative += power * coefficient * (x ** (power - 1) if power > 1 else ONE)
    return value, derivative


def solve(rows):
    # Own bounded partial-pivot Gaussian elimination of the exchange equations.
    count = len(rows)
    for column in range(count):
        pivot = max(range(column, count), key=lambda i: abs(rows[i][column]))
        rows[column], rows[pivot] = rows[pivot], rows[column]
        divisor = rows[column][column]
        if divisor == 0:
            raise ArithmeticError("singular exchange system")
        rows[column] = [value / divisor for value in rows[column]]
        for row in range(count):
            if row == column:
                continue
            factor = rows[row][column]
            rows[row] = [a - factor * b for a, b in zip(rows[row], rows[column])]
    return [row[-1] for row in rows]


def approximation_error(kind, coefficients, x, relative):
    value, slope = target(kind, x)
    p, dp = polynomial(coefficients, x)
    if relative:
        return (value - p) / value, (p * slope - dp * value) / (value * value)
    return value - p, slope - dp


def extrema(kind, coefficients, right, relative):
    def derivative(x):
        return approximation_error(kind, coefficients, x, relative)[1]

    candidates = [ZERO, right]
    left, previous = ZERO, derivative(ZERO)
    for index in range(1, 257):
        next_x = right * index / 256
        current = derivative(next_x)
        if previous * current < 0:
            low, high, low_value = left, next_x, previous
            for _ in range(200):
                middle = (low + high) / 2
                middle_value = derivative(middle)
                if low_value * middle_value > 0:
                    low, low_value = middle, middle_value
                else:
                    high = middle
                if high - low < D("1e-50"):
                    break
            candidates.append((low + high) / 2)
        left, previous = next_x, current
    candidates.sort()
    alternating = []
    for x in candidates:
        error = approximation_error(kind, coefficients, x, relative)[0]
        if abs(error) < D("1e-55"):
            continue
        if alternating and error * alternating[-1][1] > 0:
            if abs(error) > abs(alternating[-1][1]):
                alternating[-1] = (x, error)
        else:
            alternating.append((x, error))
    return alternating


def derive(kind, fixed, right, relative=False):
    powers = list(range(1, 12, 2)) if kind == "sin" else list(range(0, 9 if kind in ("cosc", "cosroot") else 11, 2))
    free = [power for power in powers if power not in fixed]
    count = len(free) + 1
    # A deterministic starting grid is not a sample fit. All subsequent nodes
    # are independently located extrema of the analytic error function.
    includes_zero = 0 in free
    nodes = [right * D(i if includes_zero else i + 1) / D(count - 1 if includes_zero else count)
             for i in range(count)]
    for iteration in range(30):
        rows = [[x ** p if p else ONE for p in free] + [D((-1) ** i) * (target(kind, x)[0] if relative else ONE),
                 target(kind, x)[0] - polynomial(fixed, x)[0]] for i, x in enumerate(nodes)]
        values = solve(rows)
        coefficients = dict(fixed)
        coefficients.update(zip(free, values[:-1]))
        peaks = extrema(kind, coefficients, right, relative)
        if len(peaks) != count:
            raise ArithmeticError(f"{kind} fixed={fixed}: expected {count} alternating extrema, got {len(peaks)}")
        next_nodes = [x for x, _ in peaks]
        magnitudes = [abs(error) for _, error in peaks]
        spread = max(magnitudes) - min(magnitudes)
        if spread < D("1e-48"):
            return {"kind": kind, "relative": relative, "fixed": {str(k): str(v) for k, v in fixed.items()},
                    "domain": ["0", str(right)], "iterations": iteration + 1,
                    "coefficients": {str(p): str(coefficients[p]) for p in powers},
                    "binary32": {str(p): f"{bits(float(coefficients[p])):08X}" for p in powers},
                    "extrema": [[str(x), str(error)] for x, error in peaks],
                    "equioscillationSpread": str(spread)}
        nodes = next_nodes
    raise ArithmeticError("exchange iteration budget")


def bits(value):
    return struct.unpack("<I", struct.pack("<f", value))[0]


def binary32(value):
    return struct.unpack("<f", struct.pack("<f", value))[0]


def from_bits(value):
    return struct.unpack("<f", struct.pack("<I", int(value, 16)))[0]


def evaluate_binary32(candidate, x):
    real_coefficients = {int(p): D(c) for p, c in candidate["coefficients"].items()}
    if candidate["kind"] == "cosroot":
        expanded = dict(real_coefficients)
        for power, coefficient in real_coefficients.items():
            expanded[power + 2] = expanded.get(power + 2, ZERO) - coefficient / (PI / 2) ** 2
        real_coefficients = expanded
    coefficients = {p: binary32(float(c)) for p, c in real_coefficients.items()}
    powers = sorted(coefficients, reverse=True)
    square = binary32(x * x)
    result = coefficients[powers[0]]
    for power in powers[1:]:
        result = binary32(binary32(result * square) + coefficients[power])
    if candidate["kind"] in ("sin", "sinc"):
        result = binary32(result * x)
    elif candidate["kind"] == "cosc":
        result = binary32(binary32(result * square) + 1.0)
    return result


def fold_radians(x):
    # Mathematical periodicity and reflection, with each proposed operation
    # explicitly binary32. This is a candidate, not an asserted SDK algorithm.
    pi = binary32(float(PI))
    half_pi = binary32(pi * .5)
    if x >= pi:
        x = binary32(x - binary32(pi * 2))
    elif x <= -pi:
        x = binary32(x + binary32(pi * 2))
    cosine_sign = 1.0
    if x > half_pi:
        x = binary32(pi - x)
        cosine_sign = -1.0
    elif x < -half_pi:
        x = binary32(-pi - x)
        cosine_sign = -1.0
    return x, cosine_sign


def compare_central_receipt(candidates, path, all_quadrants=False):
    # The derivation has already finished and cannot consume any observations.
    # Restrict to the central interval to isolate polynomial evaluation from
    # the still separate original range-reduction contract.
    import hashlib
    with open(path, "rb") as stream:
        original = stream.read()
    receipt = json.loads(original)
    rows = [row for row in receipt["Cases"] if row["Status"] == 1 and
            row["Name"].startswith("rotate-") and
            (all_quadrants or abs(from_bits(row["FloatBits"][6])) <= binary32(float(PI / 2)))]
    results = []
    for candidate in candidates:
        mismatches = []
        for row in rows:
            x = from_bits(row["FloatBits"][6])
            is_cosine = candidate["kind"] in ("cos", "cosc", "cosroot")
            sign = 1.0
            if all_quadrants:
                x, sign = fold_radians(x)
            actual = int(row["FloatBits"][8 if is_cosine else 9], 16)
            predicted = bits(evaluate_binary32(candidate, x) * (sign if is_cosine else 1.0))
            if actual != predicted:
                mismatches.append({"name": row["Name"], "radians": row["FloatBits"][6],
                                   "original": f"{actual:08X}", "derived": f"{predicted:08X}"})
        results.append({"kind": candidate["kind"], "relative": candidate["relative"], "fixed": candidate["fixed"],
                        "compared": len(rows), "mismatches": len(mismatches), "first": mismatches[:4]})
    return {"sha256": hashlib.sha256(original).hexdigest(), "allQuadrants": all_quadrants, "comparisons": results,
            "qualification": "Owned black-box comparison only; no observed coefficients or parameter fitting"}


def compare_analytic_tangent(path):
    import hashlib
    with open(path, "rb") as stream:
        original = stream.read()
    rows = [row for row in json.loads(original)["Cases"] if row["Status"] == 1 and row["Name"].startswith("skew-")]
    mismatches = []
    for row in rows:
        for radians_index, core_index in ((6, 12), (7, 9)):
            radians = from_bits(row["FloatBits"][radians_index])
            sine, cosine = analytic_sincos(D(radians))
            predicted = bits(float(sine / cosine))
            # Decimal's exact series does not retain IEEE signed zero through
            # addition. Analytic tangent is odd and preserves the input zero.
            if radians == 0:
                predicted = bits(radians)
            actual = int(row["FloatBits"][core_index], 16)
            if predicted != actual:
                mismatches.append({"name": row["Name"], "axis": radians_index - 6,
                                   "original": f"{actual:08X}", "analytic": f"{predicted:08X}"})
    return {"sha256": hashlib.sha256(original).hexdigest(), "comparedLanes": 2 * len(rows),
            "mismatches": len(mismatches), "first": mismatches[:8], "qualifiedProductCases": 0}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--family", choices=["sin", "cos", "sinc", "cosc", "cosroot", "all"], default="all")
    parser.add_argument("--relative", action="store_true")
    parser.add_argument("--observations")
    parser.add_argument("--all-quadrants", action="store_true")
    parser.add_argument("--analytic-tangent", action="store_true")
    args = parser.parse_args()
    if args.analytic_tangent:
        if not args.observations:
            parser.error("--analytic-tangent requires --observations")
        print(json.dumps(compare_analytic_tangent(args.observations), indent=2))
        return
    if args.relative and args.family in ("sin", "cos", "all"):
        parser.error("relative analysis requires a target with no zeros: sinc, cosc or cosroot")
    cases = [("sin", {}), ("sin", {1: ONE}), ("cos", {}),
             ("cos", {0: ONE}), ("cos", {0: ONE, 2: D("-.5")}),
             ("sinc", {}), ("sinc", {0: ONE}), ("cosc", {}), ("cosc", {0: D("-.5")}),
             ("cosroot", {}), ("cosroot", {0: ONE})]
    result = []
    for kind, fixed in cases:
        if args.family not in ("all", kind):
            continue
        result.append(derive(kind, fixed, PI / 2, args.relative))
    output = {"schema": 1, "qualifiedProductCases": 0,
                      "method": "Analytic Decimal Taylor target; constrained Remez exchange; no observed-data input",
                      "precision": getcontext().prec, "pi": str(PI), "candidates": result}
    if args.observations:
        output["observations"] = compare_central_receipt(result, args.observations, args.all_quadrants)
    print(json.dumps(output, indent=2))


if __name__ == "__main__":
    main()
