# -*- coding: utf-8 -*-
"""
vexyon_calc - مكتبة حسابات هندسية مختبرة لـ VEXYON.
القواعد: كل الدوال بوحدات واضحة في اسم الباراميتر (m, mm, kN, kN_m, MPa, mm4...).
أي مدخل غير منطقي بيرمي ValueError برسالة واضحة. كل دالة بترجّع Result (قاموس نتائج + المدخلات + الافتراضات)
واطبعها بـ print(...) عشان تشوف المدخلات والنتايج مع بعض.
المكتبة بتحسب ميكانيكا أساسية بس (مش كود تصميم). مراجعة مهندس مرخّص مطلوبة.
"""
import math

__version__ = "1.0"


class Result(dict):
    """نتائج الحساب (dict) + المدخلات والافتراضات للطباعة."""

    def __init__(self, name, inputs, results, notes=()):
        super().__init__(results)
        self.name, self.inputs, self.notes = name, dict(inputs), list(notes)

    def __str__(self):
        lines = [f"== {self.name} =="]
        lines.append("Inputs:")
        lines += [f"  {k} = {_f(v)}" for k, v in self.inputs.items()]
        lines.append("Results:")
        lines += [f"  {k} = {_f(v)}" for k, v in self.items()]
        if self.notes:
            lines.append("Assumptions:")
            lines += [f"  - {n}" for n in self.notes]
        return "\n".join(lines)


def _f(v):
    if isinstance(v, float):
        return f"{v:.6g}"
    return str(v)


def _pos(name, v, allow_zero=False):
    if not isinstance(v, (int, float)) or isinstance(v, bool) or math.isnan(v) or math.isinf(v):
        raise ValueError(f"{name} لازم يكون رقم (اتبعت: {v!r})")
    if v < 0 or (v == 0 and not allow_zero):
        raise ValueError(f"{name} لازم يكون {'>= 0' if allow_zero else '> 0'} (اتبعت: {v})")
    return float(v)


def _ei(E_MPa, I_mm4):
    """EI بوحدة N.mm2 لو الاتنين اتحددوا، وإلا None."""
    if E_MPa is None and I_mm4 is None:
        return None
    if E_MPa is None or I_mm4 is None:
        raise ValueError("للترخيم لازم تدخل E_MPa و I_mm4 مع بعض")
    return _pos("E_MPa", E_MPa) * _pos("I_mm4", I_mm4)


# ------------------------------------------------------------------ beams
def beam_simply_supported_udl(L_m, w_kN_m, E_MPa=None, I_mm4=None):
    """كمرة بسيطة، حمل موزع منتظم w على كل الطول. بترجّع الردود والقص والعزم الأقصى (+ الترخيم لو E و I)."""
    L, w = _pos("L_m", L_m), _pos("w_kN_m", w_kN_m, True)
    res = {"R_each_kN": w * L / 2, "V_max_kN": w * L / 2, "M_max_kNm": w * L ** 2 / 8}
    ei = _ei(E_MPa, I_mm4)
    if ei:
        res["delta_max_mm"] = 5 * (w) * (L * 1000) ** 4 / (384 * ei)  # kN/m == N/mm
    return Result("Simply supported beam - UDL",
                  {"L_m": L, "w_kN_m": w, "E_MPa": E_MPa, "I_mm4": I_mm4}, res,
                  ["Prismatic beam, linear elastic, self-weight NOT included", "Max moment and deflection at midspan"])


def _ss_point_y(x, L, a, P, EI):
    """ترخيم (mm) عند x لكمرة بسيطة وحمل P عند a. كل الأطوال mm، P بالنيوتن."""
    b = L - a
    if x <= a:
        return P * b * x * (L * L - b * b - x * x) / (6 * EI * L)
    xm = L - x
    return P * a * xm * (L * L - a * a - xm * xm) / (6 * EI * L)


def beam_simply_supported_point(L_m, P_kN, a_m, E_MPa=None, I_mm4=None):
    """كمرة بسيطة، حمل مركز P على بعد a من الركيزة اليسرى."""
    L, P, a = _pos("L_m", L_m), _pos("P_kN", P_kN, True), _pos("a_m", a_m, True)
    if a > L:
        raise ValueError("a_m لازم يكون <= L_m")
    b = L - a
    RA, RB = P * b / L, P * a / L
    res = {"R_left_kN": RA, "R_right_kN": RB, "V_max_kN": max(RA, RB), "M_max_kNm": P * a * b / L}
    ei = _ei(E_MPa, I_mm4)
    if ei:
        Lmm, amm, Pn = L * 1000, a * 1000, P * 1000
        n = 4000
        best = max((_ss_point_y(Lmm * i / n, Lmm, amm, Pn, ei), Lmm * i / n) for i in range(n + 1))
        res["delta_max_mm"] = best[0]
        res["delta_max_at_x_m"] = best[1] / 1000
        res["delta_under_load_mm"] = _ss_point_y(amm, Lmm, amm, Pn, ei)
    return Result("Simply supported beam - point load",
                  {"L_m": L, "P_kN": P, "a_m": a, "E_MPa": E_MPa, "I_mm4": I_mm4}, res,
                  ["Prismatic beam, linear elastic, self-weight NOT included", "Max moment under the load"])


def beam_cantilever_udl(L_m, w_kN_m, E_MPa=None, I_mm4=None):
    """كابولي، حمل موزع منتظم. العزم الأقصى عند الركيزة (قيمة سالبة بالاصطلاح، هنا بنرجّع القيمة المطلقة)."""
    L, w = _pos("L_m", L_m), _pos("w_kN_m", w_kN_m, True)
    res = {"V_max_kN": w * L, "M_max_kNm": w * L ** 2 / 2}
    ei = _ei(E_MPa, I_mm4)
    if ei:
        res["delta_tip_mm"] = w * (L * 1000) ** 4 / (8 * ei)
    return Result("Cantilever - UDL", {"L_m": L, "w_kN_m": w, "E_MPa": E_MPa, "I_mm4": I_mm4}, res,
                  ["Fixed at one end, free at the other", "Magnitudes only (hogging moment at support)"])


def beam_cantilever_point(L_m, P_kN, a_m=None, E_MPa=None, I_mm4=None):
    """كابولي، حمل مركز P على بعد a من الركيزة المثبتة (الافتراضي: طرف الكابولي)."""
    L = _pos("L_m", L_m)
    a = L if a_m is None else _pos("a_m", a_m, True)
    P = _pos("P_kN", P_kN, True)
    if a > L:
        raise ValueError("a_m لازم يكون <= L_m")
    res = {"V_max_kN": P, "M_max_kNm": P * a}
    ei = _ei(E_MPa, I_mm4)
    if ei:
        amm, Lmm = a * 1000, L * 1000
        res["delta_tip_mm"] = P * 1000 * amm ** 2 * (3 * Lmm - amm) / (6 * ei)
    return Result("Cantilever - point load", {"L_m": L, "P_kN": P, "a_m": a, "E_MPa": E_MPa, "I_mm4": I_mm4}, res,
                  ["Fixed at one end, free at the other", "Magnitudes only (hogging moment at support)"])


def beam_fixed_fixed_udl(L_m, w_kN_m, E_MPa=None, I_mm4=None):
    """كمرة مثبتة الطرفين، حمل موزع منتظم."""
    L, w = _pos("L_m", L_m), _pos("w_kN_m", w_kN_m, True)
    res = {"V_max_kN": w * L / 2, "M_support_kNm": w * L ** 2 / 12, "M_midspan_kNm": w * L ** 2 / 24}
    ei = _ei(E_MPa, I_mm4)
    if ei:
        res["delta_max_mm"] = w * (L * 1000) ** 4 / (384 * ei)
    return Result("Fixed-fixed beam - UDL", {"L_m": L, "w_kN_m": w, "E_MPa": E_MPa, "I_mm4": I_mm4}, res,
                  ["Both ends fully fixed against rotation and translation", "Magnitudes only (hogging at supports)"])


# --------------------------------------------------------------- sections
def section_rectangle(b_mm, h_mm):
    """مقطع مستطيل: b العرض، h الارتفاع (باتجاه الانحناء)."""
    b, h = _pos("b_mm", b_mm), _pos("h_mm", h_mm)
    A, I = b * h, b * h ** 3 / 12
    return Result("Rectangle section", {"b_mm": b, "h_mm": h},
                  {"A_mm2": A, "I_x_mm4": I, "I_y_mm4": h * b ** 3 / 12, "S_x_mm3": b * h ** 2 / 6,
                   "r_x_mm": math.sqrt(I / A), "y_c_mm": h / 2},
                  ["x-axis = horizontal centroidal axis (bending about it)"])


def section_circle(d_mm):
    d = _pos("d_mm", d_mm)
    I = math.pi * d ** 4 / 64
    A = math.pi * d ** 2 / 4
    return Result("Solid circle section", {"d_mm": d},
                  {"A_mm2": A, "I_mm4": I, "S_mm3": math.pi * d ** 3 / 32, "r_mm": d / 4})


def section_tube(D_mm, t_mm):
    """مقطع دائري مفرغ: D القطر الخارجي، t السُمك."""
    D, t = _pos("D_mm", D_mm), _pos("t_mm", t_mm)
    if 2 * t >= D:
        raise ValueError("t_mm لازم يكون أقل من نص القطر الخارجي")
    d = D - 2 * t
    I = math.pi * (D ** 4 - d ** 4) / 64
    A = math.pi * (D ** 2 - d ** 2) / 4
    return Result("Circular tube section", {"D_mm": D, "t_mm": t},
                  {"A_mm2": A, "I_mm4": I, "S_mm3": I / (D / 2), "r_mm": math.sqrt(I / A)})


def section_i_symmetric(bf_mm, tf_mm, h_mm, tw_mm):
    """مقطع I متماثل: bf عرض الشفة، tf سُمك الشفة، h الارتفاع الكلي، tw سُمك الوب."""
    bf, tf, h, tw = _pos("bf_mm", bf_mm), _pos("tf_mm", tf_mm), _pos("h_mm", h_mm), _pos("tw_mm", tw_mm)
    hw = h - 2 * tf
    if hw <= 0:
        raise ValueError("h_mm لازم يكون أكبر من 2 × tf_mm")
    if tw > bf:
        raise ValueError("tw_mm لازم يكون <= bf_mm")
    A = 2 * bf * tf + hw * tw
    I = (bf * h ** 3 - (bf - tw) * hw ** 3) / 12
    return Result("Symmetric I-section", {"bf_mm": bf, "tf_mm": tf, "h_mm": h, "tw_mm": tw},
                  {"A_mm2": A, "I_x_mm4": I, "S_x_mm3": I / (h / 2), "r_x_mm": math.sqrt(I / A)},
                  ["Sharp corners (no root fillets)"])


# ---------------------------------------------------------------- stresses
def bending_stress_MPa(M_kNm, S_mm3):
    """إجهاد الانحناء = M / S."""
    M, S = _pos("M_kNm", M_kNm, True), _pos("S_mm3", S_mm3)
    return Result("Bending stress", {"M_kNm": M, "S_mm3": S}, {"sigma_MPa": M * 1e6 / S})


def axial_stress_MPa(N_kN, A_mm2):
    N, A = _pos("N_kN", N_kN, True), _pos("A_mm2", A_mm2)
    return Result("Axial stress", {"N_kN": N, "A_mm2": A}, {"sigma_MPa": N * 1e3 / A})


def shear_stress_rect_MPa(V_kN, b_mm, h_mm):
    """إجهاد القص في مقطع مستطيل: المتوسط والأقصى (1.5 × المتوسط)."""
    V, b, h = _pos("V_kN", V_kN, True), _pos("b_mm", b_mm), _pos("h_mm", h_mm)
    avg = V * 1e3 / (b * h)
    return Result("Shear stress - rectangle", {"V_kN": V, "b_mm": b, "h_mm": h},
                  {"tau_avg_MPa": avg, "tau_max_MPa": 1.5 * avg})


def euler_buckling_kN(E_MPa, I_mm4, L_m, K=1.0):
    """حمل أويلر الحرج = pi^2 E I / (K L)^2. K: 1.0 مفصلي-مفصلي، 0.5 مثبت-مثبت، 2.0 كابولي."""
    E, I, L, K = _pos("E_MPa", E_MPa), _pos("I_mm4", I_mm4), _pos("L_m", L_m), _pos("K", K)
    Pcr = math.pi ** 2 * E * I / (K * L * 1000) ** 2 / 1000
    return Result("Euler buckling load", {"E_MPa": E, "I_mm4": I, "L_m": L, "K": K}, {"P_cr_kN": Pcr},
                  ["Elastic buckling of an ideal slender column, NO safety factor", "I = weaker axis moment of inertia"])


def utilization(demand, capacity):
    """نسبة الاستخدام demand/capacity. لو > 1 يبقى مش آمن."""
    d, c = _pos("demand", demand, True), _pos("capacity", capacity)
    r = d / c
    return Result("Utilization ratio", {"demand": d, "capacity": c}, {"ratio": r, "status": "OK" if r <= 1 else "NOT OK"},
                  ["Compare values in the SAME units; apply code safety factors yourself"])


# ------------------------------------------------------------------- units
_UNITS = {
    "length": {"mm": 1e-3, "cm": 1e-2, "m": 1.0, "km": 1e3, "in": 0.0254, "ft": 0.3048},
    "force": {"N": 1.0, "kN": 1e3, "MN": 1e6, "kgf": 9.80665, "tf": 9806.65, "lbf": 4.4482216152605, "kip": 4448.2216152605},
    "stress": {"Pa": 1.0, "kPa": 1e3, "MPa": 1e6, "GPa": 1e9, "kgf/cm2": 98066.5, "psi": 6894.757293168, "ksi": 6894757.293168},
    "moment": {"N.m": 1.0, "kN.m": 1e3, "tf.m": 9806.65, "kgf.m": 9.80665, "kip.ft": 1355.8179483314},
    "area": {"mm2": 1e-6, "cm2": 1e-4, "m2": 1.0, "in2": 0.00064516},
}
_ALIAS = {"kn": "kN", "mn": "MN", "pa": "Pa", "kpa": "kPa", "mpa": "MPa", "gpa": "GPa", "n": "N",
          "kgf/cm²": "kgf/cm2", "kg/cm2": "kgf/cm2", "kn.m": "kN.m", "knm": "kN.m", "n.m": "N.m", "nm": "N.m",
          "kn·m": "kN.m", "tf·m": "tf.m", "ton": "tf", "mm²": "mm2", "cm²": "cm2", "m²": "m2"}


def _find(u):
    u = u.strip()
    u = _ALIAS.get(u.lower(), u)
    for dim, table in _UNITS.items():
        if u in table:
            return dim, table[u]
        for k in table:
            if k.lower() == u.lower():
                return dim, table[k]
    raise ValueError(f"وحدة مش معروفة: {u!r}. الوحدات المدعومة: " + ", ".join(k for t in _UNITS.values() for k in t))


def convert(value, from_unit, to_unit):
    """تحويل وحدات (طول، قوة، إجهاد، عزم، مساحة). مثال: convert(250, 'kgf/cm2', 'MPa')."""
    if not isinstance(value, (int, float)) or isinstance(value, bool):
        raise ValueError("value لازم يكون رقم")
    d1, f1 = _find(from_unit)
    d2, f2 = _find(to_unit)
    if d1 != d2:
        raise ValueError(f"مش ينفع تحوّل من {from_unit} ({d1}) إلى {to_unit} ({d2})")
    return value * f1 / f2


def help():
    """بيطبع الدوال المتاحة."""
    names = ["beam_simply_supported_udl(L_m, w_kN_m, E_MPa=None, I_mm4=None)",
             "beam_simply_supported_point(L_m, P_kN, a_m, E_MPa=None, I_mm4=None)",
             "beam_cantilever_udl(L_m, w_kN_m, E_MPa=None, I_mm4=None)",
             "beam_cantilever_point(L_m, P_kN, a_m=None, E_MPa=None, I_mm4=None)",
             "beam_fixed_fixed_udl(L_m, w_kN_m, E_MPa=None, I_mm4=None)",
             "section_rectangle(b_mm, h_mm)", "section_circle(d_mm)", "section_tube(D_mm, t_mm)",
             "section_i_symmetric(bf_mm, tf_mm, h_mm, tw_mm)",
             "bending_stress_MPa(M_kNm, S_mm3)", "axial_stress_MPa(N_kN, A_mm2)",
             "shear_stress_rect_MPa(V_kN, b_mm, h_mm)", "euler_buckling_kN(E_MPa, I_mm4, L_m, K=1.0)",
             "utilization(demand, capacity)", "convert(value, from_unit, to_unit)"]
    print("vexyon_calc v" + __version__ + "\n" + "\n".join("  " + n for n in names))
