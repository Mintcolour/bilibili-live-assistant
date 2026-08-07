from config import AppConfig, DEFAULT_CONFIG
from settings_dialog import SettingsDialog


def test_reset_defaults_restores_display(qapp):
    cfg = AppConfig()
    cfg.display.font_size = 60
    dialog = SettingsDialog(cfg)
    dialog.reset_defaults()
    assert cfg.display.font_size == DEFAULT_CONFIG["display"]["font_size"]
    assert dialog.font_size_spin.value() == DEFAULT_CONFIG["display"]["font_size"]
