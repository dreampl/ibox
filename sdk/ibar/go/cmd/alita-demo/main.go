package main

import (
	"fmt"
	"os"

	"example.com/ibar"
)

func main() {
	bar := ibar.New("alita", os.Getenv("IBAR_TOKEN"))
	token, err := bar.Register(ibar.Manifest{Schema: 1, ID: "alita", Name: "Alita", Actions: []ibar.Action{{ID: "docs", Title: "Docs", URL: "https://example.com/alita"}}})
	if err != nil || token == "" {
		fmt.Println("approve on the notch, then run again with IBAR_TOKEN set:", err)
		return
	}
	fmt.Println("token:", token)
	cpu := 0.42
	_ = bar.Chip("#22C55E", "3")
	_ = bar.Panel([]ibar.Widget{{Kind: "number", Label: "Tasks", Value: "12"}, {Kind: "bar", Label: "CPU", Fraction: &cpu}}, []string{"docs"})
	_ = bar.Notify("Alita finished a task", "3 files summarised.", "docs")

	// A task on the activity circle: unknown at first (the ring turns), then exact progress, then done.
	_ = bar.Start(ibar.Activity{Task: "summary-1", Title: "Summarising 3 files", Kind: "task"})
	for i := 1; i <= 3; i++ {
		_ = bar.Update(ibar.Activity{Task: "summary-1", Progress: ibar.F(float64(i) / 3), Accuracy: "exact", Detail: fmt.Sprintf("file %d of 3", i)})
	}
	_ = bar.End("summary-1", false)
	_ = bar.Sound("done")
}