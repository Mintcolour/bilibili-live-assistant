from wizard import build_config_from_fields


def test_build_config_from_link():
    cfg = build_config_from_fields(
        "https://live.bilibili.com/12722549?spm_id_from=333",
        "SESS", "BUV", "JCT", 30, 150, 0.9,
    )
    assert cfg.room_id == 12722549
    assert cfg.cookie.SESSDATA == "SESS"
    assert cfg.cookie.buvid3 == "BUV"
    assert cfg.cookie.bili_jct == "JCT"
    assert cfg.display.font_size == 30
    assert cfg.display.scroll_speed == 150
    assert cfg.display.opacity == 0.9


def test_build_config_invalid_room_zero():
    cfg = build_config_from_fields("abc", "", "", "", 28, 120, 0.85)
    assert cfg.room_id == 0


def test_build_config_strips_cookie_whitespace():
    cfg = build_config_from_fields("123456", "  SESS  ", "", "", 28, 120, 0.85)
    assert cfg.cookie.SESSDATA == "SESS"
