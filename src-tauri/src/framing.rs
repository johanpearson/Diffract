//! The sidecar protocol is one JSON-RPC message per line, so a message may not contain a line break.
//! JSON escapes line breaks inside strings, so a message from `JSON.stringify` never has one.

/// The bytes to write to the sidecar's stdin for one message: the message and a `\n`.
pub fn frame(message: &str) -> Result<Vec<u8>, String> {
    if message.contains(['\n', '\r']) {
        return Err("A sidecar message must be a single line".into());
    }
    let mut bytes = Vec::with_capacity(message.len() + 1);
    bytes.extend_from_slice(message.as_bytes());
    bytes.push(b'\n');
    Ok(bytes)
}

/// One line the sidecar printed, without its line ending. Invalid UTF-8 is replaced, never dropped.
pub fn unframe(line: &[u8]) -> String {
    String::from_utf8_lossy(line).trim_end_matches(['\n', '\r']).to_string()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn frame_appends_one_newline() {
        assert_eq!(frame(r#"{"id":1}"#).unwrap(), b"{\"id\":1}\n");
    }

    #[test]
    fn frame_keeps_escaped_line_breaks_and_non_ascii() {
        let message = r#"{"path":"C:\\räksmörgås","text":"a\nb"}"#;
        assert_eq!(frame(message).unwrap(), format!("{message}\n").into_bytes());
    }

    #[test]
    fn frame_rejects_a_raw_line_break() {
        assert!(frame("{\"a\":1}\n{\"b\":2}").is_err());
        assert!(frame("{\"a\":1}\r").is_err());
    }

    #[test]
    fn unframe_strips_unix_and_windows_line_endings() {
        assert_eq!(unframe(b"{\"id\":1}\n"), "{\"id\":1}");
        assert_eq!(unframe(b"{\"id\":1}\r\n"), "{\"id\":1}");
        assert_eq!(unframe(b"{\"id\":1}"), "{\"id\":1}");
    }

    #[test]
    fn unframe_keeps_utf8_and_replaces_invalid_bytes() {
        assert_eq!(unframe("åäö\n".as_bytes()), "åäö");
        assert_eq!(unframe(b"a\xffb"), "a\u{fffd}b");
    }
}
