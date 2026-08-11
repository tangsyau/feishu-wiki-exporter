import assert from "node:assert/strict";
import test from "node:test";
import {
  resolveOrderedListNumber,
  type OrderedListNumberingState
} from "../src/ordered-list.ts";

function next(sequence: string | null | undefined, state: OrderedListNumberingState): number {
  const value = resolveOrderedListNumber(sequence, state);
  state.lastNumber = value;
  state.previousWasOrdered = true;
  return value;
}

test("旧离线包的连续 ordered 块推断为 1、2、3", () => {
  const state: OrderedListNumberingState = { lastNumber: null, previousWasOrdered: false };
  assert.deepEqual([next(undefined, state), next(undefined, state), next(undefined, state)], [1, 2, 3]);
});

test("缺少 sequence 的列表在普通块后重新从 1 开始", () => {
  const state: OrderedListNumberingState = { lastNumber: null, previousWasOrdered: false };
  assert.equal(next(undefined, state), 1);
  assert.equal(next(undefined, state), 2);
  state.previousWasOrdered = false;
  assert.equal(next(undefined, state), 1);
});

test("显式编号和 auto 支持手动起始与跨普通块继续编号", () => {
  const state: OrderedListNumberingState = { lastNumber: null, previousWasOrdered: false };
  assert.equal(next("3", state), 3);
  assert.equal(next("auto", state), 4);
  state.previousWasOrdered = false;
  assert.equal(next("auto", state), 5);
  assert.equal(next("1", state), 1);
});

test("嵌套列表使用独立的编号状态", () => {
  const outer: OrderedListNumberingState = { lastNumber: null, previousWasOrdered: false };
  const inner: OrderedListNumberingState = { lastNumber: null, previousWasOrdered: false };
  assert.equal(next("1", outer), 1);
  assert.deepEqual([next("1", inner), next("auto", inner)], [1, 2]);
  assert.equal(next("auto", outer), 2);
});
