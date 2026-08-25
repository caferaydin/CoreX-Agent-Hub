/**
 * Who the Hub records as having done something.
 *
 * The Hub is a single-user local tool: there is no sign-in, no identity provider and no session,
 * and Phase 9 deliberately does not add one. But the API still asks who acted, because an audit
 * trail that said nothing about the actor would be a worse record than one that says "the person
 * at this machine, through the browser".
 *
 * So it is a constant, stated once. Repeating the literal at four call sites would make it look
 * like a value somebody chose per action, and inventing a login to fill it would be building an
 * authentication system to satisfy a string.
 */
export const OPERATOR_ACTOR = 'hub:operator:ui';
