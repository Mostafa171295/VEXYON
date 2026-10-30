# -*- coding: utf-8 -*-
"""
vexyon_math - مكتبة معادلات وحسابات عامة (رياضيات، إحصاء، هندسة مستوية، فيزياء، مالية، تحويل وحدات).
مكتوبة بالمكتبة القياسية بس. المدخلات غير المنطقية بترمي ValueError برسالة واضحة.
الدوال بترجّع Result (اطبعها بـ print عشان تشوف المدخلات والنتايج).
الزوايا بالدرجات في الدوال (projectile) وبالراديان داخل calc() زي sin و cos.
"""
import ast
import cmath
import math
import operator
import statistics

from vexyon_calc import Result, convert as _convert_eng

__version__ = "1.1"


def _num(name, v):
    if isinstance(v, bool) or not isinstance(v, (int, float)) or math.isnan(v) or math.isinf(v):
        raise ValueError(f"{name} لازم يكون رقم (اتبعت: {v!r})")
    return v


def _posv(name, v, zero=False):
    v = _num(name, v)
    if v < 0 or (v == 0 and not zero):
        raise ValueError(f"{name} لازم يكون {'>= 0' if zero else '> 0'} (اتبعت: {v})")
    return v


# ------------------------------------------------------------ safe calculator
_FUNCS = {
    "sqrt": math.sqrt, "sin": math.sin, "cos": math.cos, "tan": math.tan,
    "asin": math.asin, "acos": math.acos, "atan": math.atan, "atan2": math.atan2,
    "sinh": math.sinh, "cosh": math.cosh, "tanh": math.tanh,
    "log": math.log, "ln": math.log, "log10": math.log10, "log2": math.log2, "exp": math.exp,
    "abs": abs, "round": round, "floor": math.floor, "ceil": math.ceil, "factorial": math.factorial,
    "min": min, "max": max, "sum": sum, "pow": pow, "radians": math.radians, "degrees": math.degrees,
    "gcd": math.gcd, "hypot": math.hypot, "comb": math.comb, "perm": math.perm,
}
_CONST = {"pi": math.pi, "e": math.e, "tau": math.tau}
_BIN = {ast.Add: operator.add, ast.Sub: operator.sub, ast.Mult: operator.mul, ast.Div: operator.truediv,
        ast.FloorDiv: operator.floordiv, ast.Mod: operator.mod, ast.Pow: operator.pow}
_UN = {ast.UAdd: operator.pos, ast.USub: operator.neg}


def _eval(node, env):
    if isinstance(node, ast.Expression):
        return _eval(node.body, env)
    if isinstance(node, ast.Constant) and isinstance(node.value, (int, float)) and not isinstance(node.value, bool):
        return node.value
    if isinstance(node, ast.Name):
        if node.id in env:
            return env[node.id]
        if node.id in _CONST:
            return _CONST[node.id]
        raise ValueError(f"اسم مش معروف: {node.id}")
    if isinstance(node, ast.BinOp) and type(node.op) in _BIN:
        a, b = _eval(node.left, env), _eval(node.right, env)
        if isinstance(node.op, ast.Pow) and abs(b) > 10000:
            raise ValueError("الأس كبير جداً")
        return _BIN[type(node.op)](a, b)
    if isinstance(node, ast.UnaryOp) and type(node.op) in _UN:
        return _UN[type(node.op)](_eval(node.operand, env))
    if isinstance(node, ast.Call) and isinstance(node.func, ast.Name) and node.func.id in _FUNCS and not node.keywords:
        return _FUNCS[node.func.id](*[_eval(a, env) for a in node.args])
    raise ValueError("تعبير غير مسموح. المسموح: أرقام وعمليات + - * / // % ** ^ ودوال زي sqrt و sin و log")


def calc(expr, **variables):
    """يحسب تعبير رياضي بأمان. مثال: calc('2*(3+4)^2/7') أو calc('x**2+1', x=3). ^ معناها أس. sin/cos بالراديان."""
    if not isinstance(expr, str) or not expr.strip():
        raise ValueError("التعبير فاضي")
    if len(expr) > 500:
        raise ValueError("التعبير طويل")
    tree = ast.parse(expr.replace("^", "**").strip(), mode="eval")
    return _eval(tree, variables)


def _fn(f):
    """يقبل دالة بايثون أو نص بالمتغير x."""
    if callable(f):
        return f
    if isinstance(f, str):
        return lambda x: calc(f, x=x)
    raise ValueError("الدالة لازم تكون نص بالمتغير x (مثلاً 'x**2-4') أو دالة")


# --------------------------------------------------------------------- algebra
def solve_linear(a, b):
    """يحل a*x + b = 0."""
    a, b = _num("a", a), _num("b", b)
    if a == 0:
        raise ValueError("a = 0: " + ("مفيش حل" if b != 0 else "عدد لا نهائي من الحلول"))
    return Result("Linear equation  a*x + b = 0", {"a": a, "b": b}, {"x": -b / a})


def solve_quadratic(a, b, c):
    """يحل a*x^2 + b*x + c = 0 (جذور حقيقية أو مركبة)."""
    a, b, c = _num("a", a), _num("b", b), _num("c", c)
    if a == 0:
        return solve_linear(b, c)
    d = b * b - 4 * a * c
    if d >= 0:
        s = math.sqrt(d)
        # صيغة مستقرة عددياً
        q = -0.5 * (b + math.copysign(s, b if b != 0 else 1))
        x1, x2 = (q / a, c / q) if q != 0 else (0.0, 0.0)
        r = sorted([x1, x2])
        kind = "two equal real roots" if d == 0 else "two real roots"
        res = {"discriminant": d, "x1": r[0], "x2": r[1], "type": kind}
    else:
        s = cmath.sqrt(d)
        res = {"discriminant": d, "x1": (-b - s) / (2 * a), "x2": (-b + s) / (2 * a), "type": "two complex roots"}
    return Result("Quadratic equation  a*x^2 + b*x + c = 0", {"a": a, "b": b, "c": c}, res)


def solve_system(A, b):
    """يحل نظام معادلات خطية A*x = b (حذف جاوس مع pivoting). A قايمة صفوف، b قايمة."""
    n = len(A)
    if n == 0 or len(b) != n or any(len(r) != n for r in A):
        raise ValueError("A لازم تكون مصفوفة مربعة n×n و b طولها n")
    M = [[float(_num("A", v)) for v in row] + [float(_num("b", b[i]))] for i, row in enumerate(A)]
    for i in range(n):
        p = max(range(i, n), key=lambda r: abs(M[r][i]))
        if abs(M[p][i]) < 1e-12:
            raise ValueError("النظام مفيش له حل وحيد (المصفوفة singular)")
        M[i], M[p] = M[p], M[i]
        for r in range(i + 1, n):
            f = M[r][i] / M[i][i]
            for c in range(i, n + 1):
                M[r][c] -= f * M[i][c]
    x = [0.0] * n
    for i in range(n - 1, -1, -1):
        x[i] = (M[i][n] - sum(M[i][j] * x[j] for j in range(i + 1, n))) / M[i][i]
    return Result("Linear system A*x = b", {"A": A, "b": b}, {"x": x})


def find_root(f, lo, hi, tol=1e-10):
    """يلاقي جذر f(x)=0 بين lo و hi بالتنصيف (لازم f(lo) و f(hi) إشارتهم مختلفة). f نص بالمتغير x مثل 'x**3-2*x-5'."""
    g = _fn(f)
    lo, hi = float(_num("lo", lo)), float(_num("hi", hi))
    if lo >= hi:
        raise ValueError("lo لازم يكون أقل من hi")
    flo, fhi = g(lo), g(hi)
    if flo == 0:
        return Result("Root finding (bisection)", {"f": f, "lo": lo, "hi": hi}, {"x": lo, "f(x)": 0.0})
    if fhi == 0:
        return Result("Root finding (bisection)", {"f": f, "lo": lo, "hi": hi}, {"x": hi, "f(x)": 0.0})
    if flo * fhi > 0:
        raise ValueError("f(lo) و f(hi) نفس الإشارة: مفيش جذر مضمون في الفترة دي. غيّر الفترة.")
    for _ in range(300):
        mid = (lo + hi) / 2
        fm = g(mid)
        if fm == 0 or (hi - lo) / 2 < tol:
            break
        if flo * fm < 0:
            hi = mid
        else:
            lo, flo = mid, fm
    return Result("Root finding (bisection)", {"f": f, "lo": lo, "hi": hi}, {"x": mid, "f(x)": g(mid)},
                  ["Finds ONE root inside the interval"])


# -------------------------------------------------------------------- calculus
def derivative(f, x, h=1e-5):
    """مشتقة عددية (فرق مركزي) عند x."""
    g = _fn(f)
    x, h = float(_num("x", x)), _posv("h", h)
    d = (g(x + h) - g(x - h)) / (2 * h)
    return Result("Numerical derivative", {"f": f, "x": x, "h": h}, {"f'(x)": d},
                  ["Central difference, accuracy about 1e-8 for smooth functions"])


def integral(f, a, b, n=1000):
    """تكامل محدد عددي بقاعدة سيمبسون من a إلى b. n زوجي."""
    g = _fn(f)
    a, b = float(_num("a", a)), float(_num("b", b))
    n = int(_posv("n", n))
    if n % 2:
        n += 1
    h = (b - a) / n
    s = g(a) + g(b)
    for i in range(1, n):
        s += g(a + i * h) * (4 if i % 2 else 2)
    return Result("Definite integral (Simpson)", {"f": f, "a": a, "b": b, "n": n}, {"integral": s * h / 3})


# ------------------------------------------------------------------ statistics
def describe(data):
    """إحصاء وصفي لقايمة أرقام."""
    if not data:
        raise ValueError("القايمة فاضية")
    d = [float(_num("data", v)) for v in data]
    res = {"n": len(d), "mean": statistics.fmean(d), "median": statistics.median(d), "min": min(d), "max": max(d),
           "sum": math.fsum(d), "stdev_population": statistics.pstdev(d)}
    if len(d) > 1:
        res["stdev_sample"] = statistics.stdev(d)
        res["variance_sample"] = statistics.variance(d)
    return Result("Descriptive statistics", {"data": data}, res)


def linear_regression(x, y):
    """انحدار خطي y = slope*x + intercept مع r^2 ومعامل الارتباط."""
    if len(x) != len(y) or len(x) < 2:
        raise ValueError("x و y لازم يبقوا نفس الطول (2 على الأقل)")
    xs, ys = [float(_num("x", v)) for v in x], [float(_num("y", v)) for v in y]
    mx, my = statistics.fmean(xs), statistics.fmean(ys)
    sxx = sum((a - mx) ** 2 for a in xs)
    if sxx == 0:
        raise ValueError("كل قيم x متساوية: مفيش انحدار")
    sxy = sum((a - mx) * (b - my) for a, b in zip(xs, ys))
    syy = sum((b - my) ** 2 for b in ys)
    slope = sxy / sxx
    res = {"slope": slope, "intercept": my - slope * mx}
    if syy > 0:
        r = sxy / math.sqrt(sxx * syy)
        res["correlation_r"], res["r_squared"] = r, r * r
    return Result("Linear regression", {"x": x, "y": y}, res, ["Least squares"])


# -------------------------------------------------------------------- geometry
def circle(r):
    r = _posv("r", r)
    return Result("Circle", {"r": r}, {"area": math.pi * r * r, "circumference": 2 * math.pi * r, "diameter": 2 * r})


def triangle_area(a, b, c):
    """مساحة مثلث من 3 أضلاع (هيرون)."""
    a, b, c = _posv("a", a), _posv("b", b), _posv("c", c)
    if a + b <= c or a + c <= b or b + c <= a:
        raise ValueError("الأضلاع دي مش بتكوّن مثلث")
    s = (a + b + c) / 2
    return Result("Triangle (Heron)", {"a": a, "b": b, "c": c},
                  {"area": math.sqrt(s * (s - a) * (s - b) * (s - c)), "perimeter": a + b + c})


def sphere(r):
    r = _posv("r", r)
    return Result("Sphere", {"r": r}, {"volume": 4 / 3 * math.pi * r ** 3, "surface_area": 4 * math.pi * r * r})


def cylinder(r, h):
    r, h = _posv("r", r), _posv("h", h)
    return Result("Cylinder", {"r": r, "h": h},
                  {"volume": math.pi * r * r * h, "lateral_area": 2 * math.pi * r * h,
                   "total_area": 2 * math.pi * r * (r + h)})


def cone(r, h):
    r, h = _posv("r", r), _posv("h", h)
    sl = math.hypot(r, h)
    return Result("Cone", {"r": r, "h": h},
                  {"volume": math.pi * r * r * h / 3, "slant_height": sl, "total_area": math.pi * r * (r + sl)})


def polygon_area(points):
    """مساحة مضلع من نقاط [(x,y),...] بالترتيب (shoelace)."""
    if len(points) < 3:
        raise ValueError("محتاج 3 نقاط على الأقل")
    pts = [(float(_num("x", p[0])), float(_num("y", p[1]))) for p in points]
    s = sum(pts[i][0] * pts[(i + 1) % len(pts)][1] - pts[(i + 1) % len(pts)][0] * pts[i][1] for i in range(len(pts)))
    return Result("Polygon area (shoelace)", {"points": points}, {"area": abs(s) / 2},
                  ["Points must follow the boundary in order, polygon must not self-intersect"])


# --------------------------------------------------------------------- physics
def projectile(v0, angle_deg, h0=0.0, g=9.81):
    """مقذوف: السرعة الابتدائية v0 (m/s)، الزاوية بالدرجات، الارتفاع الابتدائي h0 (m)."""
    v0, h0, g = _posv("v0", v0, True), _posv("h0", h0, True), _posv("g", g)
    ang = _num("angle_deg", angle_deg)
    if not -90 <= ang <= 90:
        raise ValueError("angle_deg بين -90 و 90")
    th = math.radians(ang)
    vx, vy = v0 * math.cos(th), v0 * math.sin(th)
    t = (vy + math.sqrt(vy * vy + 2 * g * h0)) / g
    return Result("Projectile motion", {"v0_m_s": v0, "angle_deg": ang, "h0_m": h0, "g": g},
                  {"time_of_flight_s": t, "range_m": vx * t, "max_height_m": h0 + (vy * vy / (2 * g) if vy > 0 else 0.0),
                   "impact_speed_m_s": math.hypot(vx, vy - g * t)},
                  ["No air resistance", "Flat ground at height 0"])


def kinetic_energy(m_kg, v_m_s):
    m, v = _posv("m_kg", m_kg), _num("v_m_s", v_m_s)
    return Result("Kinetic energy", {"m_kg": m, "v_m_s": v}, {"E_J": 0.5 * m * v * v})


# --------------------------------------------------------------------- finance
def percent_change(old, new):
    old, new = _num("old", old), _num("new", new)
    if old == 0:
        raise ValueError("القيمة القديمة = 0")
    return Result("Percent change", {"old": old, "new": new}, {"change_pct": (new - old) / abs(old) * 100})


def percent_of(p, x):
    return Result("Percent of", {"p_pct": p, "x": x}, {"value": _num("p", p) / 100 * _num("x", x)})


def compound_interest(principal, rate_pct, years, n_per_year=1):
    """فايدة مركبة: A = P (1 + r/n)^(n t)."""
    P, r, t, n = _posv("principal", principal), _num("rate_pct", rate_pct), _posv("years", years, True), int(_posv("n_per_year", n_per_year))
    A = P * (1 + r / 100 / n) ** (n * t)
    return Result("Compound interest", {"principal": P, "rate_pct": r, "years": t, "n_per_year": n},
                  {"final_amount": A, "interest_earned": A - P})


def loan_payment(principal, rate_pct, years, payments_per_year=12):
    """القسط الدوري لقرض بفايدة ثابتة (amortization)."""
    P, r, y, m = _posv("principal", principal), _posv("rate_pct", rate_pct, True), _posv("years", years), int(_posv("payments_per_year", payments_per_year))
    n, i = round(y * m), r / 100 / m
    pay = P / n if i == 0 else P * i / (1 - (1 + i) ** -n)
    return Result("Loan payment", {"principal": P, "rate_pct": r, "years": y, "payments_per_year": m},
                  {"payment": pay, "number_of_payments": n, "total_paid": pay * n, "total_interest": pay * n - P},
                  ["Fixed rate, equal payments at period end, no fees"])


# ----------------------------------------------------------------- number theory
def prime_factors(n):
    n = int(_posv("n", n))
    if n < 2:
        raise ValueError("n لازم >= 2")
    f, p, m = [], 2, n
    while p * p <= m:
        while m % p == 0:
            f.append(p); m //= p
        p += 1 if p == 2 else 2
    if m > 1:
        f.append(m)
    return Result("Prime factorization", {"n": n}, {"factors": f, "is_prime": len(f) == 1})


def gcd_lcm(a, b):
    a, b = int(_posv("a", a)), int(_posv("b", b))
    return Result("GCD / LCM", {"a": a, "b": b}, {"gcd": math.gcd(a, b), "lcm": a * b // math.gcd(a, b)})


# ------------------------------------------------------------------------ units
_T = {"C", "F", "K"}
_GEN = {
    "mass": {"mg": 1e-6, "g": 1e-3, "kg": 1.0, "ton": 1000.0, "lb": 0.45359237, "oz": 0.028349523125},
    "time": {"s": 1.0, "min": 60.0, "h": 3600.0, "day": 86400.0, "week": 604800.0},
    "speed": {"m/s": 1.0, "km/h": 1 / 3.6, "mph": 0.44704, "knot": 0.514444444444},
    "volume": {"mL": 1e-6, "cm3": 1e-6, "L": 1e-3, "m3": 1.0, "gal": 0.003785411784, "ft3": 0.028316846592},
}


def _gen_find(u):
    u = u.strip()
    for dim, t in _GEN.items():
        for k, f in t.items():
            if k.lower() == u.lower():
                return dim, f
    raise ValueError("unknown")


def convert_units(value, from_unit, to_unit):
    """تحويل: حرارة (C,F,K)، كتلة، زمن، سرعة، حجم، وكمان الطول والقوة والإجهاد والعزم والمساحة. مثال: convert_units(100,'C','F')."""
    v = _num("value", value)
    a, b = from_unit.strip(), to_unit.strip()
    if a.upper() in _T or b.upper() in _T:
        if not (a.upper() in _T and b.upper() in _T):
            raise ValueError("التحويل بين وحدة حرارة ووحدة تانية غير ممكن")
        a, b = a.upper(), b.upper()
        k = v if a == "K" else (v + 273.15 if a == "C" else (v - 32) * 5 / 9 + 273.15)
        if k < 0:
            raise ValueError("درجة الحرارة تحت الصفر المطلق")
        return k if b == "K" else (k - 273.15 if b == "C" else (k - 273.15) * 9 / 5 + 32)
    try:
        (d1, f1), (d2, f2) = _gen_find(a), _gen_find(b)
        if d1 != d2:
            raise ValueError(f"مش ينفع تحوّل من {a} ({d1}) إلى {b} ({d2})")
        return v * f1 / f2
    except ValueError as e:
        if str(e) != "unknown":
            raise
    return _convert_eng(v, a, b)


# --------------------------------------------------------------------- verification
def _tol(name, tol):
    tol = _num(name, tol)
    if tol <= 0:
        raise ValueError("tol لازم يكون أكبر من صفر")
    return tol


def verify_root(f, x, tol=1e-6):
    """يعوّض x في f (نص بالمتغير x) ويتأكد إن f(x) قريبة من صفر. ok=True يعني الحل اتحقق."""
    tol = _tol("tol", tol)
    fx = _fn(f)(x)
    ok = abs(fx) <= tol * max(1.0, abs(x))
    return Result("Verify root: f(x) = 0", {"x": x, "tol": tol}, {"ok": ok, "f(x)": fx})


def verify_quadratic(a, b, c, roots, tol=1e-6):
    """يعوّض كل جذر في a*x^2 + b*x + c ويتأكد إنه صفر (بالنسبة لحجم الحدود). roots قايمة."""
    a, b, c, tol = _num("a", a), _num("b", b), _num("c", c), _tol("tol", tol)
    if not roots:
        raise ValueError("roots فاضية")
    res, all_ok = {}, True
    for i, x in enumerate(roots, 1):
        r = a * x * x + b * x + c
        scale = abs(a) * abs(x) ** 2 + abs(b) * abs(x) + abs(c)
        ok = abs(r) <= tol * max(1.0, scale)
        all_ok = all_ok and ok
        res[f"residual_{i}"] = abs(r)
    res["ok"] = all_ok
    return Result("Verify quadratic roots", {"a": a, "b": b, "c": c, "roots": list(roots), "tol": tol}, res)


def verify_system(A, b, x, tol=1e-6):
    """يحسب A*x ويقارنه بـ b؛ ok=True لو أكبر فرق صغير."""
    tol = _tol("tol", tol)
    n = len(A)
    if n == 0 or len(b) != n or len(x) != n or any(len(r) != n for r in A):
        raise ValueError("A مربعة n×n و b و x طولهم n")
    diffs = [abs(sum(float(A[i][j]) * x[j] for j in range(n)) - float(b[i])) for i in range(n)]
    scale = max([1.0] + [abs(float(v)) for v in b])
    return Result("Verify linear system  A*x = b", {"x": list(x), "tol": tol},
                  {"ok": max(diffs) <= tol * scale, "max_residual": max(diffs)})


def verify_equal(got, expected, tol=1e-6):
    """يقارن رقمين (نتيجة وقيمة متوقعة من طريقة تانية) بخطأ نسبي tol."""
    got, expected, tol = _num("got", got), _num("expected", expected), _tol("tol", tol)
    err = abs(got - expected)
    return Result("Verify equal", {"got": got, "expected": expected, "tol": tol},
                  {"ok": err <= tol * max(abs(expected), 1e-9) + 1e-12, "abs_error": err})


def help():
    print("vexyon_math v" + __version__ + """
  calc(expr, **vars)                       تعبير رياضي آمن، مثال calc('2*(3+4)^2/7')
  solve_linear(a,b)  solve_quadratic(a,b,c)  solve_system(A,b)
  find_root(f,lo,hi)                       f نص بالمتغير x
  derivative(f,x)  integral(f,a,b)
  describe(data)  linear_regression(x,y)
  circle(r) triangle_area(a,b,c) sphere(r) cylinder(r,h) cone(r,h) polygon_area(points)
  projectile(v0,angle_deg,h0=0)  kinetic_energy(m_kg,v_m_s)
  percent_change(old,new)  percent_of(p,x)  compound_interest(P,rate_pct,years,n)  loan_payment(P,rate_pct,years)
  prime_factors(n)  gcd_lcm(a,b)
  convert_units(value, from, to)           حرارة وكتلة وزمن وسرعة وحجم وطول وقوة وإجهاد
  تحقق من الحل (ok=True يعني اتحقق): verify_root(f,x)  verify_quadratic(a,b,c,roots)  verify_system(A,b,x)  verify_equal(got,expected)""")
