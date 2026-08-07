from config import AppConfig, CookieConfig, needs_onboarding


def test_default_config_needs_onboarding():
    assert needs_onboarding(AppConfig()) is True


def test_configured_config_no_onboarding():
    cfg = AppConfig(room_id=12722549, cookie=CookieConfig(SESSDATA="x"))
    assert needs_onboarding(cfg) is False


def test_partial_config_no_onboarding():
    # 只有房间号也算已配置（跳过向导）
    assert needs_onboarding(AppConfig(room_id=12722549)) is False
