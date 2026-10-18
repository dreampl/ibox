// Package ibar is a thin IBAR plugin client: one JSON object per line over IBAR's named pipe (schema 1).
package ibar

import (
	"bufio"
	"encoding/json"
	"errors"
	"os"
	"os/user"
	"regexp"
	"strings"
)

type Action struct {
	ID      string   `json:"id"`
	Title   string   `json:"title"`
	Command string   `json:"command,omitempty"`
	Args    []string `json:"args,omitempty"`
	URL     string   `json:"url,omitempty"`
}

type Manifest struct {
	Schema    int      `json:"schema"`
	ID        string   `json:"id"`
	Name      string   `json:"name"`
	Publisher string   `json:"publisher,omitempty"`
	Icon      string   `json:"icon,omitempty"`
	Actions   []Action `json:"actions,omitempty"`
}

type Widget struct {
	Kind     string    `json:"kind"`
	Label    string    `json:"label,omitempty"`
	Value    string    `json:"value,omitempty"`
	Fraction *float64  `json:"fraction,omitempty"`
	Items    []string  `json:"items,omitempty"`
	Values   []float64 `json:"values,omitempty"`
}

type Reply struct {
	Status string `json:"status"`
	Token  string `json:"token,omitempty"`
	Error  string `json:"error,omitempty"`
}

type Client struct {
	Pipe, Plugin, Token string
}

func New(plugin, token string) *Client {
	pipe := os.Getenv("IBAR_PIPE")
	if pipe == "" {
		name := "user"
		if u, err := user.Current(); err == nil {
			name = u.Username
			if i := strings.LastIndex(name, `\`); i >= 0 {
				name = name[i+1:]
			}
		}
		name = strings.ToLower(regexp.MustCompile(`[^A-Za-z0-9]`).ReplaceAllString(name, ""))
		if len(name) > 32 {
			name = name[:32]
		}
		pipe = "ibar-" + name
	}
	return &Client{Pipe: pipe, Plugin: plugin, Token: token}
}

func (c *Client) Send(req map[string]any) (*Reply, error) {
	req["v"] = 1
	f, err := os.OpenFile(`\\.\pipe\`+c.Pipe, os.O_RDWR, 0)
	if err != nil {
		return nil, err
	}
	defer f.Close()
	data, _ := json.Marshal(req)
	if _, err := f.Write(append(data, '\n')); err != nil {
		return nil, err
	}
	line, err := bufio.NewReader(f).ReadBytes('\n')
	if err != nil {
		return nil, err
	}
	var r Reply
	if err := json.Unmarshal(line, &r); err != nil {
		return nil, err
	}
	if r.Status != "ok" && r.Status != "pending" {
		return &r, errors.New(r.Error)
	}
	return &r, nil
}

func (c *Client) Register(m Manifest) (string, error) {
	r, err := c.Send(map[string]any{"op": "register", "plugin": m.ID, "token": c.Token, "manifest": m})
	if err == nil && r.Token != "" {
		c.Token = r.Token
	}
	return c.Token, err
}

func (c *Client) op(op string, fields map[string]any) error {
	fields["op"], fields["plugin"], fields["token"] = op, c.Plugin, c.Token
	_, err := c.Send(fields)
	return err
}

func (c *Client) Notify(title, body, link string) error {
	fields := map[string]any{"title": title}
	if body != "" {
		fields["body"] = body
	}
	if link != "" {
		fields["link"] = link
	}
	return c.op("notify", fields)
}

// Activity is one task on the notch's activity circle. Leave Progress nil when the amount is unknown (the ring
// turns, no number is shown); with a Progress (0..1) give its Accuracy: "exact", "good" or "estimate".
type Activity struct {
	Task     string   `json:"task"`
	Title    string   `json:"title,omitempty"`
	Kind     string   `json:"kind,omitempty"`
	Detail   string   `json:"detail,omitempty"`
	State    string   `json:"state,omitempty"`
	Progress *float64 `json:"progress,omitempty"`
	Accuracy string   `json:"accuracy,omitempty"`
	Done     *int64   `json:"done,omitempty"`
	Total    *int64   `json:"total,omitempty"`
	Rate     *float64 `json:"rate,omitempty"`
	Color    string   `json:"color,omitempty"`
}

func (c *Client) activity(op string, a Activity) error {
	data, _ := json.Marshal(a)
	fields := map[string]any{}
	_ = json.Unmarshal(data, &fields)
	return c.op(op, fields)
}

// Start shows a new task (Title is required).
func (c *Client) Start(a Activity) error { return c.activity("activity.start", a) }

// Update changes a task (state: running, paused or waiting).
func (c *Client) Update(a Activity) error { return c.activity("activity.update", a) }

// End finishes a task; its circle goes back into the notch.
func (c *Client) End(task string, failed bool) error {
	state := "done"
	if failed {
		state = "failed"
	}
	return c.op("activity.end", map[string]any{"task": task, "state": state})
}

// Sound plays one of IBAR's sounds: "start", "done" or "error".
func (c *Client) Sound(kind string) error { return c.op("sound", map[string]any{"sound": kind}) }

// F is a helper for the optional numbers: ibar.F(0.4).
func F(v float64) *float64 { return &v }

// N is a helper for the optional byte counts: ibar.N(1024).
func N(v int64) *int64 { return &v }

func (c *Client) Chip(color, value string) error {
	return c.op("chip", map[string]any{"color": color, "value": value})
}

func (c *Client) Panel(widgets []Widget, buttons []string) error {
	return c.op("panel", map[string]any{"widgets": widgets, "buttons": buttons})
}