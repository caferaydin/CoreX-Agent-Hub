import '@testing-library/jest-dom/vitest';

// jsdom has no EventSource. Tests that exercise the live stream install their own; the rest
// get an absence, which the hook already treats as "no live updates" rather than an error.
