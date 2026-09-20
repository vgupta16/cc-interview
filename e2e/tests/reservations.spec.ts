import {
  AMENITY,
  BUILDING_A,
  bookViaForm,
  ensureAmenityOpen,
  expect,
  expectBookingError,
  findReservation,
  gotoApp,
  openAmenity,
  selectResident,
  submitBooking,
  test,
  ui,
} from './helpers';

// Test titles start with S<n>: the number picks the scenario's private day (see helpers.ts).

test.describe('booking and capacity', () => {
  test('S1 book a Party Room slot as alice', async ({ page, scenario }) => {
    await gotoApp(page);
    await openAmenity(page, AMENITY.partyRoomA);

    await bookViaForm(page, { start: scenario.at(10), minutes: 60, resident: 'alice' });

    await expect(ui.allRows(page)).toHaveCount(1);
    await expect(ui.allRows(page).first()).toContainText('Alice');
    await expect(ui.bookError(page)).toHaveCount(0);
  });

  test('S2 double-booking the Party Room (capacity 1) is rejected with slot_full', async ({ page, scenario }) => {
    await gotoApp(page);
    await openAmenity(page, AMENITY.partyRoomA);
    await bookViaForm(page, { start: scenario.at(10), minutes: 60, resident: 'alice' });

    await selectResident(page, 'bob');
    await ensureAmenityOpen(page, AMENITY.partyRoomA);
    await submitBooking(page, { start: scenario.at(10, 30), minutes: 60 }); // overlaps 10:30-11:00

    await expectBookingError(page, 'slot_full');
    await expect(ui.rowsOf(page, 'bob')).toHaveCount(0);
    await expect(ui.rowsOf(page, 'alice')).toHaveCount(1);
  });

  test('S3 Gym (capacity 2): alice and bob fit, carol is rejected', async ({ page, scenario }) => {
    const start = scenario.at(10);
    await gotoApp(page);
    await openAmenity(page, AMENITY.gymA);
    await bookViaForm(page, { start, minutes: 60, resident: 'alice' });

    await selectResident(page, 'bob');
    await ensureAmenityOpen(page, AMENITY.gymA);
    await bookViaForm(page, { start, minutes: 60, resident: 'bob' });

    await selectResident(page, 'carol');
    await ensureAmenityOpen(page, AMENITY.gymA);
    await submitBooking(page, { start, minutes: 60 });

    await expectBookingError(page, 'slot_full');
    await expect(ui.rowsOf(page, 'carol')).toHaveCount(0);
    await expect(ui.rowsOf(page, 'alice')).toHaveCount(1);
    await expect(ui.rowsOf(page, 'bob')).toHaveCount(1);
  });

  test('S4 back-to-back bookings are allowed (10-11 then 11-12)', async ({ page, scenario }) => {
    await gotoApp(page);
    await openAmenity(page, AMENITY.partyRoomA);
    await bookViaForm(page, { start: scenario.at(10), minutes: 60, resident: 'alice' });

    await selectResident(page, 'bob');
    await ensureAmenityOpen(page, AMENITY.partyRoomA);
    await bookViaForm(page, { start: scenario.at(11), minutes: 60, resident: 'bob' });

    await expect(ui.rowsOf(page, 'alice')).toHaveCount(1);
    await expect(ui.rowsOf(page, 'bob')).toHaveCount(1);
    await expect(ui.bookError(page)).toHaveCount(0);
  });
});

test.describe('cancel and ownership', () => {
  test('S5 alice cancels her booking, then bob can take the same slot', async ({ page, request, scenario }) => {
    const start = scenario.at(10);
    await gotoApp(page);
    await openAmenity(page, AMENITY.partyRoomA);
    await bookViaForm(page, { start, minutes: 60, resident: 'alice' });

    const alice = await findReservation(request, {
      building: BUILDING_A, amenityId: AMENITY.partyRoomA, resident: 'alice', start,
    });
    await expect(ui.row(page, alice.id)).toBeVisible();
    await ui.cancelButton(page, alice.id).click();
    await expect(ui.row(page, alice.id)).toHaveCount(0);
    await expect(ui.allRows(page)).toHaveCount(0);

    await selectResident(page, 'bob');
    await ensureAmenityOpen(page, AMENITY.partyRoomA);
    await bookViaForm(page, { start, minutes: 60, resident: 'bob' });
    await expect(ui.bookError(page)).toHaveCount(0);
  });

  test("S6 bob sees alice's row but cannot cancel it", async ({ page, request, scenario }) => {
    const start = scenario.at(10);
    await gotoApp(page);
    await openAmenity(page, AMENITY.partyRoomA);
    await bookViaForm(page, { start, minutes: 60, resident: 'alice' });

    const alice = await findReservation(request, {
      building: BUILDING_A, amenityId: AMENITY.partyRoomA, resident: 'alice', start,
    });
    await expect(ui.cancelButton(page, alice.id)).toBeVisible(); // control: owner sees it

    await selectResident(page, 'bob');
    await ensureAmenityOpen(page, AMENITY.partyRoomA);
    await expect(ui.row(page, alice.id)).toBeVisible();
    await expect(ui.row(page, alice.id)).toContainText('Alice');
    await expect(ui.cancelButton(page, alice.id)).toHaveCount(0);

    // Back to alice: the button returns.
    await selectResident(page, 'alice');
    await ensureAmenityOpen(page, AMENITY.partyRoomA);
    await expect(ui.cancelButton(page, alice.id)).toBeVisible();
  });
});
