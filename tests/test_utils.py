from utils import extract_room_id


def test_extract_full_url():
    assert extract_room_id("https://live.bilibili.com/12722549") == 12722549


def test_extract_url_with_params():
    assert extract_room_id("https://live.bilibili.com/12722549?spm_id_from=333.1007") == 12722549


def test_extract_plain_number():
    assert extract_room_id("12722549") == 12722549


def test_extract_invalid():
    assert extract_room_id("") is None
    assert extract_room_id("hello world") is None
    assert extract_room_id("live.bilibili.com/abc") is None
