import {
  AMENITY,
  Scenario,
  bookViaForm,
  expect,
  expectBookingError,
  gotoApp,
  openAmenity,
  submitBooking,
  test,
  ui,
} from './helpers';

// Validation and policy errors surface in the UI with the server's stable error code.

test.describe('booking rules', () => {
  test('S7 duration beyond the Party Room maximum (240) -> max_duration_exceeded', async ({ page, scenario }) => {
    await gotoApp(page);
    await openAmenity(page, AMENITY.partyRoomA);
    await submitBooking(page, { start: scenario.at(10), minutes: 241 });

    await expectBookingError(page, 'max_duration_exceeded');
    await expect(ui.allRows(page)).toHaveCount(0);
  });

  test('S8 start time in the past -> in_past', async ({ page }) => {
    await gotoApp(page);
    await openAmenity(page, AMENITY.partyRoomA);
    await submitBooking(page, { start: Scenario.yesterdayAt(10), minutes: 60 });

    await expectBookingError(page, 'in_past');
    await expect(ui.allRows(page)).toHaveCount(0);
  });

  test('S9 one active booking per resident: second booking on the same amenity -> already_has_booking', async ({
    page,
    scenario,
  }) => {
    await gotoApp(page);
    await openAmenity(page, AMENITY.partyRoomA);
    await bookViaForm(page, { start: scenario.at(10), minutes: 60, resident: 'alice' });

    await submitBooking(page, { start: scenario.at(14), minutes: 60 }); // non-overlapping

    await expectBookingError(page, 'already_has_booking');
    await expect(ui.rowsOf(page, 'alice')).toHaveCount(1);
  });
});
