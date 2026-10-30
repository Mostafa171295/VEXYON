# -*- coding: utf-8 -*-
"""اختبارات vexyon_calc: كل قيمة متوقعة محسوبة يدوي من معادلات معروفة. لو أي اختبار فشل، ماتعتمدش على المكتبة."""
import math
import unittest
import vexyon_calc as vc

E, I = 200000.0, 5.4e9  # MPa, mm4


class Tests(unittest.TestCase):
    def near(self, a, b, tol=1e-6):
        self.assertAlmostEqual(a, b, delta=abs(b) * tol + 1e-9)

    def test_ss_udl(self):
        r = vc.beam_simply_supported_udl(6, 20, E, I)
        self.near(r["M_max_kNm"], 90); self.near(r["V_max_kN"], 60)
        self.near(r["delta_max_mm"], 5 * 20 * 6000 ** 4 / (384 * E * I))

    def test_ss_point_midspan(self):
        r = vc.beam_simply_supported_point(8, 100, 4, E, I)
        self.near(r["M_max_kNm"], 100 * 8 / 4); self.near(r["R_left_kN"], 50)
        self.near(r["delta_max_mm"], 100e3 * 8000 ** 3 / (48 * E * I), 1e-4)

    def test_ss_point_offcenter(self):
        r = vc.beam_simply_supported_point(10, 60, 3, E, I)
        self.near(r["R_left_kN"], 42); self.near(r["R_right_kN"], 18)
        self.near(r["M_max_kNm"], 60 * 3 * 7 / 10)
        self.near(r["delta_under_load_mm"], 60e3 * 3000 ** 2 * 7000 ** 2 / (3 * E * I * 10000))
        self.assertGreaterEqual(r["delta_max_mm"], r["delta_under_load_mm"])
        self.assertAlmostEqual(r["R_left_kN"] + r["R_right_kN"], 60)

    def test_cantilever(self):
        r = vc.beam_cantilever_udl(3, 10, E, I)
        self.near(r["M_max_kNm"], 45); self.near(r["V_max_kN"], 30)
        self.near(r["delta_tip_mm"], 10 * 3000 ** 4 / (8 * E * I))
        p = vc.beam_cantilever_point(3, 50, None, E, I)
        self.near(p["M_max_kNm"], 150); self.near(p["delta_tip_mm"], 50e3 * 3000 ** 3 / (3 * E * I))
        q = vc.beam_cantilever_point(3, 50, 3, E, I)
        self.near(q["delta_tip_mm"], p["delta_tip_mm"])

    def test_fixed_fixed(self):
        r = vc.beam_fixed_fixed_udl(6, 20, E, I)
        self.near(r["M_support_kNm"], 60); self.near(r["M_midspan_kNm"], 30)
        self.near(r["delta_max_mm"], 20 * 6000 ** 4 / (384 * E * I))

    def test_sections(self):
        r = vc.section_rectangle(300, 600)
        self.near(r["A_mm2"], 180000); self.near(r["I_x_mm4"], 5.4e9); self.near(r["S_x_mm3"], 1.8e7)
        c = vc.section_circle(100)
        self.near(c["I_mm4"], math.pi * 100 ** 4 / 64); self.near(c["A_mm2"], math.pi * 2500)
        t = vc.section_tube(100, 10)
        self.near(t["I_mm4"], math.pi * (100 ** 4 - 80 ** 4) / 64)
        i = vc.section_i_symmetric(200, 15, 400, 10)
        hw = 370
        self.near(i["A_mm2"], 2 * 200 * 15 + hw * 10)
        self.near(i["I_x_mm4"], 2 * (200 * 15 ** 3 / 12 + 200 * 15 * 192.5 ** 2) + 10 * hw ** 3 / 12, 1e-9)

    def test_stresses(self):
        self.near(vc.bending_stress_MPa(90, 1.8e7)["sigma_MPa"], 5.0)
        self.near(vc.axial_stress_MPa(180, 18000)["sigma_MPa"], 10.0)
        s = vc.shear_stress_rect_MPa(60, 300, 600)
        self.near(s["tau_avg_MPa"], 60e3 / 180000); self.near(s["tau_max_MPa"], 1.5 * 60e3 / 180000)

    def test_euler(self):
        r = vc.euler_buckling_kN(200000, 1e7, 3, 1.0)
        self.near(r["P_cr_kN"], math.pi ** 2 * 200000 * 1e7 / 3000 ** 2 / 1000)
        self.near(vc.euler_buckling_kN(200000, 1e7, 3, 0.5)["P_cr_kN"], 4 * r["P_cr_kN"])

    def test_utilization(self):
        self.assertEqual(vc.utilization(80, 100)["status"], "OK")
        self.assertEqual(vc.utilization(120, 100)["status"], "NOT OK")

    def test_convert(self):
        self.near(vc.convert(1, "kgf", "N"), 9.80665); self.near(vc.convert(1, "tf", "kN"), 9.80665)
        self.near(vc.convert(250, "kgf/cm2", "MPa"), 24.516625); self.near(vc.convert(1, "ksi", "MPa"), 6.894757293168)
        self.near(vc.convert(1, "in", "mm"), 25.4); self.near(vc.convert(12, "in", "ft"), 1.0)
        self.near(vc.convert(1, "tf.m", "kN.m"), 9.80665)
        with self.assertRaises(ValueError): vc.convert(1, "kN", "m")
        with self.assertRaises(ValueError): vc.convert(1, "foo", "m")

    def test_validation(self):
        for bad in (lambda: vc.beam_simply_supported_udl(-1, 10), lambda: vc.beam_simply_supported_udl(0, 10),
                    lambda: vc.beam_simply_supported_udl(5, 10, E_MPa=200000),
                    lambda: vc.beam_simply_supported_point(5, 10, 6), lambda: vc.section_tube(10, 6),
                    lambda: vc.section_i_symmetric(100, 60, 100, 10), lambda: vc.section_rectangle("a", 5),
                    lambda: vc.utilization(1, 0)):
            with self.assertRaises(ValueError): bad()

    def test_printable(self):
        text = str(vc.beam_simply_supported_udl(6, 20))
        self.assertIn("M_max_kNm", text); self.assertIn("Inputs", text)


if __name__ == "__main__":
    unittest.main(verbosity=2)
