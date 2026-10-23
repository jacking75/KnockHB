package tunnel

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"github.com/coder/websocket"
	"io"
	"unicode/utf8"
)

func readMsg(ctx context.Context, c *websocket.Conn, m *Msg) error {
	kind, data, err := c.Read(ctx)
	if err != nil {
		return err
	}
	if kind != websocket.MessageText || len(data) > readLimit || !utf8.Valid(data) {
		return errors.New("invalid text frame")
	}
	if err := validateJSON(json.NewDecoder(bytes.NewReader(data))); err != nil {
		return err
	}
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(m); err != nil {
		return err
	}
	if m.Version != 1 {
		return errors.New("unsupported protocol version")
	}
	return nil
}
func validateJSON(dec *json.Decoder) error {
	if err := jsonValue(dec, 0); err != nil {
		return err
	}
	if _, err := dec.Token(); err != io.EOF {
		return errors.New("trailing JSON data")
	}
	return nil
}
func jsonValue(dec *json.Decoder, depth int) error {
	if depth > 32 {
		return errors.New("JSON nesting limit")
	}
	token, err := dec.Token()
	if err != nil {
		return err
	}
	delimiter, ok := token.(json.Delim)
	if !ok {
		return nil
	}
	if depth >= 32 {
		return errors.New("JSON nesting limit")
	}
	switch delimiter {
	case '{':
		fields := make(map[string]bool)
		for dec.More() {
			key, err := dec.Token()
			if err != nil {
				return err
			}
			name, ok := key.(string)
			if !ok || fields[name] {
				return errors.New("duplicate or invalid JSON field")
			}
			fields[name] = true
			if err := jsonValue(dec, depth+1); err != nil {
				return err
			}
		}
	case '[':
		for dec.More() {
			if err := jsonValue(dec, depth+1); err != nil {
				return err
			}
		}
	default:
		return errors.New("invalid JSON delimiter")
	}
	_, err = dec.Token()
	return err
}
