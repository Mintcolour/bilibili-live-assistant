from styles import STYLESHEET


def test_stylesheet_has_core_selectors():
    for sel in ["QMainWindow", "QDialog", "QPushButton", "QLineEdit", "QCheckBox", "QFrame#card"]:
        assert sel in STYLESHEET
