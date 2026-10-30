# -*- coding: utf-8 -*-
"""اختبارات vexyon_math: القيم المتوقعة معروفة رياضياً."""
import math
import unittest
import vexyon_math as vm


class Tests(unittest.TestCase):
    def near(self, a, b, tol=1e-6):
        self.assertAlmostEqual(a, b, delta=abs(b) * tol + 1e-9)

    def test_calc(self):
        self.near(vm.calc("2*(3+4)^2/7"), 14)
        self.near(vm.calc("sqrt(16)+3**2"), 13)
        self.near(vm.calc("x**2+1", x=3), 10)
        self.near(vm.calc("sin(pi/2)"), 1)
        self.near(vm.calc("factorial(5)"), 120)
        self.near(vm.calc("-2**2"), -4)
        for bad in ("__import__('os')", "open('x')", "().__class__", "9**9**9", "a+1", "", "1+"):
            with self.assertRaises((ValueError, SyntaxError)):
                vm.calc(bad)

    def test_algebra(self):
        self.near(vm.solve_linear(2, -6)["x"], 3)
        q = vm.solve_quadratic(1, -3, 2)
        self.near(q["x1"], 1); self.near(q["x2"], 2)
        e = vm.solve_quadratic(1, 2, 1)
        self.near(e["x1"], -1); self.near(e["x2"], -1)
        c = vm.solve_quadratic(1, 0, 1)
        self.assertAlmostEqual(c["x2"].imag, 1.0); self.assertAlmostEqual(c["x1"].imag, -1.0)
        big = vm.solve_quadratic(1, -1e8, 1)   # استقرار عددي
        self.near(big["x1"] * big["x2"], 1, 1e-9)
        s = vm.solve_system([[2, 1], [1, 3]], [5, 10])["x"]
        self.near(s[0], 1); self.near(s[1], 3)
        with self.assertRaises(ValueError): vm.solve_system([[1, 2], [2, 4]], [1, 2])
        with self.assertRaises(ValueError): vm.solve_linear(0, 1)

    def test_roots_calculus(self):
        self.near(vm.find_root("x**2-4", 0, 5)["x"], 2, 1e-8)
        self.near(vm.find_root("x**3-2*x-5", 2, 3)["x"], 2.0945514815, 1e-8)
        with self.assertRaises(ValueError): vm.find_root("x**2+1", -1, 1)
        self.near(vm.derivative("x**3", 2)["f'(x)"], 12, 1e-6)
        self.near(vm.integral("x**2", 0, 3)["integral"], 9, 1e-9)
        self.near(vm.integral("sin(x)", 0, math.pi)["integral"], 2, 1e-9)

    def test_stats(self):
        d = vm.describe([2, 4, 4, 4, 5, 5, 7, 9])
        self.near(d["mean"], 5); self.near(d["stdev_population"], 2); self.near(d["median"], 4.5)
        r = vm.linear_regression([1, 2, 3, 4], [3, 5, 7, 9])
        self.near(r["slope"], 2); self.near(r["intercept"], 1); self.near(r["r_squared"], 1)
        with self.assertRaises(ValueError): vm.linear_regression([1, 1], [2, 3])

    def test_geometry(self):
        self.near(vm.circle(2)["area"], 4 * math.pi)
        self.near(vm.triangle_area(3, 4, 5)["area"], 6)
        with self.assertRaises(ValueError): vm.triangle_area(1, 2, 10)
        self.near(vm.sphere(3)["volume"], 36 * math.pi)
        self.near(vm.cylinder(1, 2)["volume"], 2 * math.pi)
        self.near(vm.cone(3, 4)["slant_height"], 5)
        self.near(vm.polygon_area([(0, 0), (4, 0), (4, 3), (0, 3)])["area"], 12)

    def test_physics(self):
        p = vm.projectile(20, 45)
        self.near(p["range_m"], 400 / 9.81, 1e-9); self.near(p["max_height_m"], 200 / 9.81 / 2 * 1, 1e-9)
        f = vm.projectile(0, 0, 10)
        self.near(f["time_of_flight_s"], math.sqrt(2 * 10 / 9.81), 1e-9)
        self.near(vm.kinetic_energy(2, 3)["E_J"], 9)

    def test_finance(self):
        self.near(vm.compound_interest(1000, 5, 10)["final_amount"], 1000 * 1.05 ** 10)
        self.near(vm.compound_interest(1000, 12, 1, 12)["final_amount"], 1000 * 1.01 ** 12)
        L = vm.loan_payment(100000, 6, 30)
        self.near(L["payment"], 599.5505, 1e-5)
        self.near(vm.loan_payment(1200, 0, 1)["payment"], 100)
        self.near(vm.percent_change(80, 100)["change_pct"], 25)
        self.near(vm.percent_of(15, 200)["value"], 30)

    def test_numbers(self):
        self.assertEqual(vm.prime_factors(360)["factors"], [2, 2, 2, 3, 3, 5])
        self.assertTrue(vm.prime_factors(97)["is_prime"])
        g = vm.gcd_lcm(12, 18)
        self.assertEqual((g["gcd"], g["lcm"]), (6, 36))

    def test_units(self):
        self.near(vm.convert_units(100, "C", "F"), 212); self.near(vm.convert_units(0, "C", "K"), 273.15)
        self.near(vm.convert_units(-40, "F", "C"), -40)
        self.near(vm.convert_units(1, "kg", "lb"), 2.2046226218, 1e-8)
        self.near(vm.convert_units(90, "km/h", "m/s"), 25); self.near(vm.convert_units(2, "h", "min"), 120)
        self.near(vm.convert_units(1, "gal", "L"), 3.785411784); self.near(vm.convert_units(250, "kgf/cm2", "MPa"), 24.516625)
        with self.assertRaises(ValueError): vm.convert_units(1, "kg", "m")
        with self.assertRaises(ValueError): vm.convert_units(1, "C", "m")
        with self.assertRaises(ValueError): vm.convert_units(-300, "C", "K")

    def test_verify(self):
        q = vm.solve_quadratic(1, -3, 2)
        self.assertTrue(vm.verify_quadratic(1, -3, 2, [q["x1"], q["x2"]])["ok"])
        self.assertFalse(vm.verify_quadratic(1, -3, 2, [1, 3])["ok"])
        big = vm.solve_quadratic(1, -1e8, 1)
        self.assertTrue(vm.verify_quadratic(1, -1e8, 1, [big["x1"], big["x2"]])["ok"])
        c = vm.solve_quadratic(1, 0, 1)
        self.assertTrue(vm.verify_quadratic(1, 0, 1, [c["x1"], c["x2"]])["ok"])
        r = vm.find_root("x**3-2*x-5", 2, 3)["x"]
        self.assertTrue(vm.verify_root("x**3-2*x-5", r)["ok"])
        self.assertFalse(vm.verify_root("x**3-2*x-5", 2.5)["ok"])
        A, b = [[2, 1], [1, 3]], [5, 10]
        x = vm.solve_system(A, b)["x"]
        self.assertTrue(vm.verify_system(A, b, x)["ok"])
        self.assertFalse(vm.verify_system(A, b, [1, 2])["ok"])
        self.assertTrue(vm.verify_equal(vm.circle(2)["area"], math.pi * 4)["ok"])
        self.assertFalse(vm.verify_equal(3.0, 3.1)["ok"])
        with self.assertRaises(ValueError): vm.verify_quadratic(1, 2, 3, [])
        with self.assertRaises(ValueError): vm.verify_system([[1, 2], [3, 4]], [1, 2], [1])
        with self.assertRaises(ValueError): vm.verify_equal(1, 1, tol=0)


if __name__ == "__main__":
    unittest.main(verbosity=2)
