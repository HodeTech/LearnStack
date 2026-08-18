import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import PublicHomePage from "./page";

/**
 * The point of this file is that it exists. `apps/web`'s `test` script carried
 * `--passWithNoTests` against zero test files, so the required `frontend` CI
 * check was green without asserting anything. The tolerance is removed together
 * with the first test that satisfies it, so the check never sits red across a
 * packet boundary — Phase 02a Packet 3b.
 *
 * The substantive frontend suite arrives with Phase 02d. This asserts only what
 * the placeholder page actually promises today.
 */
describe("PublicHomePage", () => {
  it("renders the platform name as the page heading", () => {
    render(<PublicHomePage />);

    expect(
      screen.getByRole("heading", { level: 1, name: "LearnStack" }),
    ).toBeInTheDocument();
  });

  it("renders the positioning line", () => {
    render(<PublicHomePage />);

    expect(
      screen.getByText(/multi-tenant core platform/i),
    ).toBeInTheDocument();
  });
});
